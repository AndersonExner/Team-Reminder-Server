# Team Reminder Server

API ASP.NET Core (.NET 10) com PostgreSQL para autenticação, alarmes pessoais e gestão de grupos. O aplicativo sincroniza os alarmes pessoais com seu cache e dispara as notificações localmente. Alarmes compartilhados e organizações ficam para as próximas etapas.

## Desenvolvimento com Docker

```powershell
Copy-Item .env.example .env
# Edite POSTGRES_PASSWORD no .env antes de iniciar.
docker compose up --build -d
Invoke-RestMethod http://localhost:5080/health/ready
```

O banco não publica porta no host. O volume `postgres-data` persiste os dados. `docker compose down` preserva o volume. O container da API executa migrations versionadas com lock transacional antes de iniciar. Não há contas ou senhas de usuário predefinidas.

## API

| Método | Caminho | Corpo / autenticação |
|---|---|---|
| POST | `/api/auth/register` | `{ "name": "Nome", "email": "nome@example.com", "password": "senha com pelo menos 10 caracteres" }` |
| POST | `/api/auth/login` | `{ "email": "nome@example.com", "password": "..." }` |
| POST | `/api/auth/refresh` | `{ "refreshToken": "..." }` |
| GET | `/api/auth/me` | `Authorization: Bearer <accessToken>` |
| POST | `/api/auth/logout` | `Authorization: Bearer <accessToken>` |

Cadastro retorna 201; login e refresh retornam 200, com `accessToken`, `refreshToken`, `accessExpiresAt`, `refreshExpiresAt` e `user` (`id`, `name`, `email`). Cadastro já conecta o usuário. Logout retorna 204 e revoga a sessão atual. Outras sessões do mesmo usuário continuam válidas.

Access token: 15 minutos. Sessão: 30 dias, sem extensão automática. Refresh troca ambos os tokens e invalida os anteriores, com controle de concorrência. Tokens aleatórios de 256 bits são armazenados apenas como hash SHA-256. Senhas usam PasswordHasher do ASP.NET Core Identity, PBKDF2 com salt e 210 mil iterações. E-mails são únicos sem distinção de maiúsculas e minúsculas. Senhas têm de 10 a 128 caracteres. Respostas de autenticação não devem ser armazenadas em cache. Endpoints de autenticação têm limite de 20 solicitações/minuto por IP visto pelo servidor.

`/health` verifica o processo; `/health/ready` verifica a conexão com o banco. Recuperação de senha e verificação de e-mail ainda não foram implementadas.

## Aplicativo Windows

A seção **Configurações → Minha conta** permite cadastro, login e logout. Configure `TEAMREMINDER_API_URL` antes de iniciar o aplicativo; o padrão local é `http://localhost:5080/`. Fora do loopback, apenas HTTPS é aceito. A sessão é protegida pelo Windows para o usuário atual e vinculada ao endereço do servidor. As senhas não são salvas.

## Deploy

O Dockerfile é independente do Compose: hospede a API com ele e use PostgreSQL gerenciado. Configure `ConnectionStrings__Database` pelo gerenciador de segredos da hospedagem, usando a string Npgsql (`Host=...;Database=...;Username=...;Password=...;SSL Mode=VerifyFull`). Nunca inclua a conexão de produção no repositório. Publique a API por HTTPS, com terminação TLS da plataforma. O container escuta na porta 8080; configure a hospedagem para essa porta. Use `/health/ready` como health check.

Render (API) e Neon (banco) são uma opção inicial com planos gratuitos sujeitos a quotas e suspensão por inatividade. A primeira chamada pode demorar. Isso não afeta os alarmes locais. Confirme os limites do plano antes de publicar.

## Testes

Os testes usam PostgreSQL real e exigem um banco exclusivo para testes. Exemplo:

```powershell
docker run --name teamreminder-auth-tests -e POSTGRES_PASSWORD=local-test-password -e POSTGRES_DB=auth_tests -p 127.0.0.1:15432:5432 -d postgres:17-alpine
$env:TEAMREMINDER_TEST_DATABASE = 'Host=localhost;Port=15432;Database=auth_tests;Username=postgres;Password=local-test-password'
dotnet test TeamReminder.Server.slnx
docker rm -f teamreminder-auth-tests
```

Os testes verificam cadastro, hashes persistidos, duplicidade, validação, login, autorização, rotação, concorrência, expiração e logout, além de persistência de alarmes, isolamento entre contas, sincronização incremental, repetição de operações, conflitos, exclusão e concorrência. Não aponte `TEAMREMINDER_TEST_DATABASE` para produção.

## Alarmes pessoais e sincronização

Todos os endpoints abaixo exigem `Authorization: Bearer <accessToken>`. A conta é extraída da sessão autenticada; o cliente não escolhe o proprietário.

| Método | Caminho | Comportamento |
|---|---|---|
| GET | `/api/alarms` | Lista os alarmes ativos da conta |
| GET | `/api/alarms/{id}` | Consulta um alarme ativo; 404 se não pertence à conta ou foi excluído |
| PUT | `/api/alarms/{id}` | Cria com versão zero ou atualiza usando a versão atual |
| DELETE | `/api/alarms/{id}?version=...&mutationId=...` | Registra uma exclusão versionada |
| GET | `/api/sync?since=0` | Retorna as alterações desde o cursor, incluindo exclusões |

Exemplo de corpo de PUT:

```json
{
  "expectedVersion": 0,
  "mutationId": "a9f786e8-7d36-4a1c-87bf-8051fc3b65b0",
  "deleted": false,
  "alarm": {
    "title": "Reunião",
    "scheduledAtUtc": "2026-10-15T13:00:00Z",
    "createdAtUtc": "2026-10-14T13:00:00Z",
    "timeZoneId": "America/Sao_Paulo",
    "recurrence": 0,
    "days": 0,
    "linkUrl": "https://example.com/meeting",
    "linkLabel": "Abrir reunião",
    "soundKind": 0
  }
}
```

`recurrence`: 0 uma vez, 1 diária, 2 semanal. `days` é uma máscara de bits (segunda=1 até domingo=64), obrigatória e não vazia apenas para semanal. `soundKind`: 0 padrão, 1 notificação, 2 exclamação, 3 crítico, 4 silencioso. Arquivos personalizados não são enviados. Datas devem ser UTC; títulos têm até 120 caracteres; links HTTP/HTTPS têm até 2048 caracteres, sem credenciais; legendas têm até 40 caracteres.

PUT retorna 200 com `{ id, version, deleted, mutationId, alarm }`. Também aceita `deleted: true` e `alarm: null`, para o envio da fila de exclusões. A mesma operação reenviada enquanto for a última daquele alarme retorna a mesma versão. Se outra operação já a substituiu, o servidor retorna conflito. Versão desatualizada retorna 409 com `{ message, current }`; cabe ao cliente pedir a resolução ao usuário. Uma exclusão pode ser revertida explicitamente usando sua versão atual e novo conteúdo.

Sincronização retorna `{ cursor, changes }`. O cursor é monotônico por conta, e os dados são lidos em um snapshot consistente. As alterações usam a última versão de cada alarme, incluindo exclusões. Cada escrita bloqueia somente a conta envolvida e incrementa sua revisão na mesma transação. O banco mantém os registros excluídos; o limite inicial é 1000 registros por conta, incluindo essas exclusões. Ao atingir o limite, novas criações retornam 422.

A migration 2 adiciona a revisão da conta e a tabela `PersonalAlarms`, preservando usuários e sessões existentes. Reiniciar o container aplica migrations pendentes. Próxima ocorrência e confirmação de disparo não são campos do servidor: pertencem ao dispositivo local.

Validação dos alarmes pessoais: 20 testes da API com PostgreSQL real, incluindo oito cenários de alarmes pessoais, e um fluxo integrado entre dois caches do cliente. Os testes da migration também rodam novamente em bancos já inicializados.

## Grupos, participantes e convites

A migration 3 adiciona `Groups`, `GroupMemberships`, `GroupInvitations` e `GroupAuditEvents`, preservando contas, sessões e alarmes existentes. A criação de um grupo e de sua participação como proprietário ocorre na mesma transação.

Papéis: 0 membro, 1 administrador e 2 proprietário. Todos podem consultar seu grupo e participantes; membros podem sair. Administradores e proprietários podem editar dados, convidar, cancelar convites, remover participantes e alterar papéis entre membro e administrador. Somente o proprietário pode transferir a propriedade ou excluir o grupo. Transferência exige um participante já aceito e torna o antigo proprietário administrador. O proprietário não pode ser removido, rebaixado ou sair antes de transferir.

Todos os endpoints exigem Bearer token. A API lê a participação atual no banco a cada operação: o papel não é confiado ao aplicativo ou incluído como autorização permanente no token.

| Método | Caminho | Corpo / parâmetro |
|---|---|---|
| GET | `/api/groups` | Grupos ativos do usuário |
| POST | `/api/groups` | `{ "name": "Desenvolvimento DigitalDesk", "description": "Equipe" }` |
| GET | `/api/groups/{id}` | Grupo, participantes e convites pendentes para administradores |
| PATCH | `/api/groups/{id}` | `{ "name": "...", "description": "...", "expectedRevision": 1 }` |
| DELETE | `/api/groups/{id}?revision=...` | Apenas proprietário |
| POST | `/api/groups/{id}/leave` | `{ "expectedRevision": 1 }` |
| POST | `/api/groups/{id}/transfer` | `{ "accountId": "...", "expectedRevision": 1 }` |
| PATCH | `/api/groups/{id}/members/{accountId}` | `{ "role": 1, "expectedRevision": 1 }` |
| DELETE | `/api/groups/{id}/members/{accountId}?revision=...` | Remover participante |
| POST | `/api/groups/{id}/invitations` | `{ "email": "colega@example.com", "expectedRevision": 1 }` |
| POST | `/api/groups/{id}/invitations/{invitationId}/cancel` | `{ "expectedRevision": 1 }` |
| GET | `/api/invitations` | Convites recebidos, pendentes e válidos |
| POST | `/api/invitations/{id}/accept` | Sem corpo; apenas destinatário |
| POST | `/api/invitations/{id}/decline` | Sem corpo; apenas destinatário |
| GET | `/api/groups/{id}/audit` | Últimas 50 ações, apenas administradores/proprietário |

Criação retorna 201; ações administrativas e respostas a convites retornam 204. Listagem de grupos retorna `{ id, name, description, ownerAccountId, myRole, revision, memberCount }`. Detalhe retorna `{ group, members, invitations }`. E-mails de participantes são visíveis aos membros do grupo. Não membros recebem 404 ao consultar um grupo, sem acesso aos dados.

Convites são destinados ao e-mail de uma conta existente, sem distinção de maiúsculas/minúsculas, e expiram em sete dias. O destinatário deve aceitar para entrar como membro; criar um convite não concede acesso. Duplicatas pendentes são recusadas. Convites expirados retornam 410 ao serem respondidos e podem ser substituídos por um novo convite. Aceite repetido é idempotente enquanto a pessoa permanece no grupo. Convite aceito não permite reingresso após remoção ou saída. O envio nesta versão é somente dentro do aplicativo, sem serviço de e-mail.

Remover ou rebaixar um administrador cancela seus convites pendentes, assim como sua saída. Exclusão do grupo é lógica: ele desaparece para participantes e destinatários, os convites pendentes são cancelados e o histórico permanece no banco.

Escritas bloqueiam a linha do grupo, revalidam papel e revisão, registram a ação e confirmam uma única transação. Versão antiga retorna 409 e exige atualização; ações negadas não avançam a revisão. Operações em contas para controlar limites usam locks que permitem as verificações de chaves estrangeiras, evitando deadlock em convites recíprocos. Limites iniciais: 20 grupos por proprietário, 100 grupos por usuário, 100 membros por grupo e 100 convites pendentes por grupo ou destinatário; ultrapassagem retorna 422. Requisições dos endpoints de grupos têm limite de 120 por minuto por conta, configurável por `Groups__RequestsPerMinute`.

Validação: 36 testes da API com PostgreSQL real, incluindo 16 cenários de grupos, permissões, convite, expiração, limites e concorrência. Também foi validado um fluxo completo usando o cliente HTTP do aplicativo com três contas, desde criação até transferência, remoção e exclusão. Alarmes compartilhados pelo grupo e organizações ainda não estão implementados.
