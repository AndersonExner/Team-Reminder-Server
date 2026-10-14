# Team Reminder Server

API ASP.NET Core (.NET 10) com PostgreSQL. Autenticação e contas são responsabilidade do servidor; os alarmes e as notificações continuam locais. Sincronização de alarmes fica para uma próxima etapa.

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

Os testes verificam cadastro, hashes persistidos, duplicidade, validação, login, autorização, rotação, concorrência, expiração e logout. Não aponte `TEAMREMINDER_TEST_DATABASE` para produção.
