# API no Render com PostgreSQL no Neon

O blueprint `render.yaml` cria apenas um Web Service gratuito no Render, usando o Dockerfile do repositório e a branch `main`. O PostgreSQL é externo, no Neon. O Compose continua sendo o ambiente local.

## Ambiente criado

API: https://team-reminder-api.onrender.com

Render: https://dashboard.render.com/web/srv-db7p0hi5b2lc738o34hg

Banco: projeto TeamReminderBeta no Neon, branch padrão production, região AWS US East 2 (Ohio). O serviço Render usa a main do repositório e o plano Free. A conexão foi configurada como variável no Render, sem gravação no Git.

O serviço criado pela integração usa a verificação padrão de porta do Render. Para usar também a verificação HTTP do banco, configure Health Check Path como /health/ready no painel; o blueprint já inclui esse caminho.

## Banco

No Neon, crie um projeto no plano Free, preferencialmente na região AWS US East 2 (Ohio), próximo à região Ohio da API. Use o banco e o usuário gerados pelo projeto. Em Connect, escolha .NET e copie a conexão Npgsql. Ela deve ter o formato:

```text
Host=SEU_HOST;Database=SEU_BANCO;Username=SEU_USUARIO;Password=SUA_SENHA;SSL Mode=VerifyFull;Channel Binding=Require
```

Configure essa conexão exclusivamente na variável secreta `ConnectionStrings__Database` do Render. Não salve credenciais no Git nem no aplicativo Windows. O servidor executa migrations na inicialização para criar/atualizar usuários, alarmes e grupos.

## API

Depois de integrar a configuração à main, no Render:

1. Conecte a conta GitHub e autorize o repositório Team-Reminder-Server.
2. Escolha New > Blueprint e selecione o repositório, branch main.
3. Confira o plano Free e o serviço team-reminder-api em Ohio.
4. Informe `ConnectionStrings__Database` quando o blueprint pedir o valor.
5. Inicie o deploy e acompanhe os logs.

Alternativamente, crie New > Web Service com runtime Docker, branch main, Dockerfile `./Dockerfile`, plano Free e health check `/health/ready`. Defina `ASPNETCORE_ENVIRONMENT=Production` e a conexão do banco. Não há Build Command ou Start Command adicional: o Dockerfile e o entrypoint cuidam disso.

O entrypoint usa a variável `PORT` da hospedagem; localmente, mantém a porta 8080. A API atende HTTP dentro do container, com HTTPS na URL pública fornecida pelo Render. Cada commit na main pode disparar um novo deploy conforme a configuração e a conexão do GitHub.

## Verificação realizada

O deploy foi confirmado como live no Render. A API pública respondeu status=ok em /health e status=ready em /health/ready. A consulta de /api/groups sem autenticação retornou 401. As oito tabelas da aplicação foram conferidas no Neon, incluindo schema_versions e os dados de grupos. Nenhum usuário de demonstração foi criado no banco hospedado.

## Conferência

Use a URL real fornecida pelo Render:

```powershell
Invoke-RestMethod 'https://SEU_SERVICO.onrender.com/health'
Invoke-RestMethod 'https://SEU_SERVICO.onrender.com/health/ready'
```

As respostas esperadas são `status=ok` e `status=ready`. Depois, teste cadastro e login no aplicativo. Para apontá-lo para a API publicada:

```powershell
$env:TEAMREMINDER_API_URL = 'https://SEU_SERVICO.onrender.com/'
dotnet run --project src/clients/TeamReminder.Windows -p:Platform=x64
```

Execute o comando do cliente no repositório Team-Reminder-Client. Os dados da conta são separados por endereço da API; a conta e os alarmes do servidor local não são migrados automaticamente para o banco hospedado.

## Limites do ambiente gratuito

O Render suspende o serviço após 15 minutos sem tráfego, e a primeira chamada seguinte pode levar cerca de um minuto. Os alarmes já em cache continuam locais. Banco, banda e execução estão sujeitos às quotas dos respectivos planos.

Não use o PostgreSQL gratuito do Render como banco permanente: ele expira após 30 dias. Este blueprint não cria esse banco nem inclui serviços pagos.

Fontes: [Render Blueprints](https://render.com/docs/blueprint-spec), [plano gratuito do Render](https://render.com/docs/free), [conexão .NET no Neon](https://neon.com/docs/guides/dotnet-npgsql), [planos do Neon](https://neon.com/pricing).
