# Página pública e distribuição

A raiz da API serve a página de apresentação. GET /api/public/statistics retorna apenas contagens agregadas: contas, grupos não excluídos e alarmes não excluídos. O resultado fica em cache por 60 segundos.

GET /api/public/release consulta a última release estável pública do repositório Team-Reminder-Client e procura o instalador TeamReminder-Setup-VERSAO-x64.exe. Cache de cinco minutos. Sem release ou instalador, retorna 404 e a página mostra download em breve. Falhas do GitHub não são tratadas como ausência de release. Quando o release.json traz um SHA-256 válido, a resposta inclui `sha256` e a página mostra o valor para conferência com Get-FileHash.

GET /download/windows redireciona para esse instalador. O executável fica no GitHub Releases e não no disco temporário do Render. O repositório e a release precisam ser públicos para download sem autenticação.

No cliente, builds Debug e Release usam https://team-reminder-api.onrender.com/. Para a API local, defina TEAMREMINDER_API_URL=http://localhost:5080/.

Após revisar e integrar as alterações do cliente, publique uma tag no commit desejado:
    git tag v0.2.0
    git push origin v0.2.0

O workflow Windows release gera o instalador e cria uma release pública. Para compilar apenas localmente:
    ./scripts/Build-Installer.ps1 -Version 0.2.0

O pacote inclui .NET e Windows App SDK. O instalador é por usuário e dispensa privilégios de administrador. Não há assinatura digital de distribuição configurada.

Após integrar as alterações do servidor na main, o Render faz o deploy automático da página. Não é preciso alterar o banco.

As releases incluem release.json com versão, data e endereço do instalador. O servidor lê esse asset público pela URL releases/latest/download/release.json, evitando limites da API anônima do GitHub em IPs compartilhados.

A página e o painel administrativo seguem a identidade do aplicativo: destaque violeta, temas claro e escuro conforme o sistema (`prefers-color-scheme`) e o ícone em /favicon.svg. A página explica a instalação, inclusive o aviso do SmartScreen enquanto o instalador não tem assinatura. No painel, a auditoria mostra o nome de quem fez a ação e do registro afetado (o identificador aparece quando o registro não existe mais), e os detalhes exibem datas, papéis, situações e recorrências por extenso.
