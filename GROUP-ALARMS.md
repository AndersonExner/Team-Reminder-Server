# Alarmes de grupos

A migração 5 adiciona GroupAlarms. Os alarmes pessoais continuam separados.

PUT /api/groups/{groupId}/alarms/{id} usa AlarmWriteRequest (expectedVersion, mutationId, deleted e alarm). Somente proprietário e administradores do grupo ativo podem publicar, editar ou excluir. DELETE na mesma rota usa version e mutationId. O grupo é bloqueado durante a operação para coordenar mudanças de papel, exclusão e suspensão. Alterações de alarmes possuem versão própria e auditam o evento no grupo.

GET /api/group-alarms retorna um snapshot completo dos alarmes não excluídos dos grupos ativos do usuário, incluindo nome do grupo, versão e permissão de edição atual. A participação precisa estar aceita. Quem entra posteriormente recebe as definições existentes; usuários removidos ou grupos suspensos deixam de aparecer no snapshot.

Limite: 100 registros por grupo, incluindo tombstones. A API exige um horário futuro para avisos únicos. Arquivos de áudio não são enviados; usam-se sons integrados ou o padrão local de cada dispositivo.

O cliente aplica snapshots no cache da conta sem inserir na fila dos alarmes pessoais. A remoção só ocorre depois de uma resposta completa e bem-sucedida. Falhas de rede preservam o cache. Comprovantes de disparo são locais e preservados para evitar repetição após atualização ou suspensão temporária do grupo. A sincronização periódica existente é de 60 segundos e também pode ser acionada em Configurações.

Não há disparo de notificações no servidor. Os dispositivos precisam sincronizar antes do horário e manter o aplicativo em execução ou na bandeja para receber os avisos locais. Sair de um grupo em outro dispositivo não interrompe um cache offline até a próxima sincronização.

Integração: revisar e integrar a API primeiro; só depois distribuir o aplicativo com a nova modal.
