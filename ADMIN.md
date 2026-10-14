# Administração do sistema

Acesse /admin. O painel usa a autenticação de sessões existente e exige IsSystemAdmin em todos os endpoints /api/admin. Essa permissão é diferente de administrador de grupo.

## Provisionamento inicial
Execute as migrações e depois o comando --bootstrap-admin, com ConnectionStrings__Database, Admin__Email, Admin__Password e opcionalmente Admin__Name definidos no ambiente. A senha é armazenada como hash e não é impressa. O comando recusa contas existentes e recusa provisionamento se já houver administrador ativo.

    dotnet TeamReminder.Server.dll --migrate
    dotnet TeamReminder.Server.dll --bootstrap-admin

Remova Admin__Password do ambiente depois do provisionamento. Não grave senhas em arquivos versionados.

## Operações
- Usuários: criar, consultar detalhes, buscar, editar, definir senha, atribuir permissão administrativa, ativar, desativar, excluir logicamente e restaurar.
- Grupos: criar, consultar membros e convites, buscar, editar, transferir propriedade por e-mail, ativar, desativar, excluir logicamente e restaurar.
- Auditoria: ações administrativas com responsável, alvo e data. Não contém senhas ou tokens.
- Listas paginadas de 20 registros. Detalhes exibem até 100 alarmes, membros ou convites, com contagem total de alarmes.

Exclusão lógica preserva os dados e o e-mail permanece reservado. Restauração deixa o registro desativado para revisão antes da ativação. Usuários proprietários de grupos não excluídos devem transferir ou excluir esses grupos antes de serem excluídos. Convites pendentes são cancelados ao desativar ou excluir usuários e grupos; não são reabertos na restauração.

Desativação, mudança de senha, e-mail ou permissão revogam sessões. A versão de sessão impede que tokens anteriores voltem a funcionar depois da reativação. O último administrador ativo e a própria conta administrativa são protegidos de remoção de acesso.

Alterações exigem a revisão atual e retornam 409 quando o registro mudou. As ações administrativas são serializadas em transações e grupos respeitam também os locks usados pelo aplicativo.

Desativar uma conta ou grupo bloqueia o acesso ao servidor. Não interrompe notificações em computadores que já tenham alarmes locais. O servidor não tem confirmação dos disparos locais.

Tokens do painel ficam apenas em memória, sem localStorage e sem cookies de autenticação. Recarregar a página exige login. O painel utiliza CSP, dados inseridos via textContent, confirmação antes de ações destrutivas e cache desabilitado.
