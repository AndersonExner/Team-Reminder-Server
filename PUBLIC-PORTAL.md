# Página pública e distribuição

A raiz da API serve a página de apresentação. GET /api/public/statistics retorna apenas contagens agregadas: contas, grupos não excluídos e alarmes não excluídos. O resultado fica em cache por 60 segundos.

GET /api/public/release consulta a última release estável pública do repositório Team-Reminder-Client e procura o instalador TeamReminder-Setup-VERSAO-x64.exe. Cache de cinco minutos. Sem release ou instalador, retorna 404 e a página mostra download em breve. Falhas do GitHub não são tratadas como ausência de release.

GET /download/windows redireciona para esse instalador. O executável fica no GitHub Releases e não no disco temporário do Render. O repositório e a release precisam ser públicos para download sem autenticação.

No cliente, builds Debug usam localhost:5080 e builds Release usam https://team-reminder-api.onrender.com/. TEAMREMINDER_API_URL continua podendo substituir esse padrão.

Após revisar e integrar as alterações do cliente, publique uma tag no commit desejado:
    git tag v0.2.0
    git push origin v0.2.0

O workflow Windows release gera o instalador e cria uma release pública. Para compilar apenas localmente:
    ./scripts/Build-Installer.ps1 -Version 0.2.0

O pacote inclui .NET e Windows App SDK. O instalador é por usuário e dispensa privilégios de administrador. Não há assinatura digital de distribuição configurada.

Após integrar as alterações do servidor na main, o Render faz o deploy automático da página. Não é preciso alterar o banco.

As releases incluem release.json com versão, data e endereço do instalador. O servidor lê esse asset público pela URL releases/latest/download/release.json, evitando limites da API anônima do GitHub em IPs compartilhados.
