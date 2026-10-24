const labels = {user:'Usuário',group:'Grupo',groups:'Grupos',members:'Membros',invitations:'Convites',alarms:'Alarmes (até 100)',alarmCount:'Total de alarmes',id:'Identificador',name:'Nome',email:'E-mail',isActive:'Ativo',isSystemAdmin:'Administrador do sistema',deletedAt:'Excluído em',revision:'Revisão',createdAt:'Criado em',ownerAccountId:'Proprietário',ownerEmail:'E-mail do proprietário',description:'Descrição',role:'Papel',title:'Título',scheduledAtUtc:'Agendado para',version:'Versão sincronizada',recurrence:'Recorrência',expiresAt:'Expira em',status:'Situação',recipientAccountId:'Destinatário'};
const actions = {Created:'Criado',Updated:'Editado',Deleted:'Excluído',Restored:'Restaurado',Bootstrap:'Administrador inicial',PasswordChanged:'Senha alterada',AccessUpdated:'Acesso atualizado',Deactivated:'Desativado'};
const roles = ['Membro','Administrador','Proprietário'];
const recurrences = ['Uma vez','Diária','Semanal'];
const invitationStatus = ['Pendente','Aceito','Recusado','Cancelado','Expirado'];
// Internal fields that only add noise to the detail view.
const hiddenKeys = new Set(['revision','groupId','groupName','canManage']);
const $ = id => document.getElementById(id);
const guid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
let session = null, tab = 'users', page = 1, items = [], selected = null, loading = false, refreshing = null, toastTimer = null;

function message(text, isError = false) {
  const toast = $('message');
  clearTimeout(toastTimer);
  toast.textContent = text;
  toast.classList.toggle('error-toast', isError);
  toast.hidden = !text;
  if (text) toastTimer = setTimeout(() => { toast.hidden = true; }, isError ? 8000 : 4000);
}
const fail = e => message(e.message, true);

function clearSession() {
  session = null; $('panel').hidden = true; $('login').hidden = false; $('logout').hidden = true; $('who').hidden = true;
  $('rows').replaceChildren(); $('summary').replaceChildren(); $('detail-content').replaceChildren();
  if ($('details').open) $('details').close();
  if ($('editor').open) $('editor').close();
}

async function api(path, options = {}) {
  if (!session) throw new Error('Entre novamente.');
  if (new Date(session.accessExpiresAt).getTime() < Date.now() + 30000) {
    if (!refreshing) refreshing = fetch('/api/auth/refresh', {method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({refreshToken:session.refreshToken})})
      .then(async r => { if (!r.ok) { clearSession(); throw new Error('Sessão expirada. Entre novamente.'); } const data = await r.json(); if (session) session = data; })
      .finally(() => refreshing = null);
    await refreshing;
  }
  if (!session) throw new Error('Entre novamente.');
  const response = await fetch('/api/admin/' + path, {...options, headers:{'Content-Type':'application/json',Authorization:'Bearer ' + session.accessToken}, cache:'no-store'});
  if (response.status === 401 || response.status === 403) { clearSession(); throw new Error('Sessão inválida ou sem permissão administrativa.'); }
  if (!response.ok) {
    const body = await response.json().catch(() => ({}));
    throw new Error(body.message || Object.values(body.errors || {}).flat().join(' ') || 'Não foi possível concluir a operação.');
  }
  return response.status === 204 ? null : response.json();
}

// Small DOM helpers: text only, never HTML, so names and e-mails cannot inject markup.
function node(tag, className, text) { const el = document.createElement(tag); if (className) el.className = className; if (text !== undefined) el.textContent = text; return el; }
function cell(row, text, className) { const td = node('td', className, text ?? '—'); row.append(td); return td; }
function badge(text, kind) { return node('span', 'badge ' + kind, text); }
function button(td, title, handler, kind = '') {
  const b = node('button', 'btn small ' + kind, title);
  b.onclick = () => handler().catch(fail);
  td.append(b);
}
const date = value => value ? new Date(value).toLocaleString('pt-BR') : '—';
const day = value => value ? new Date(value).toLocaleDateString('pt-BR') : '—';
function statusBadge(item) { return item.deletedAt ? badge('Excluído', 'bad') : item.isActive ? badge('Ativo', 'ok') : badge('Desativado', 'warn'); }

async function refreshSummary() {
  const data = await api('summary'); $('summary').replaceChildren();
  const names = {accounts:'Contas cadastradas',activeUsers:'Usuários ativos',groups:'Grupos cadastrados',activeGroups:'Grupos ativos',alarms:'Alarmes no servidor',pendingInvitations:'Convites pendentes'};
  for (const [key, label] of Object.entries(names)) {
    const box = node('article'); box.append(node('strong', '', data[key].toLocaleString('pt-BR')), node('span', '', label)); $('summary').append(box);
  }
}

function header(columns) {
  const row = node('tr');
  for (const name of columns) row.append(node('th', '', name));
  $('head').replaceChildren(row);
}

async function load() {
  if (loading) return; loading = true; $('filter').disabled = true;
  try {
    const data = await api(tab + '?' + new URLSearchParams({page, q:$('search').value, status:$('status').value}));
    if (!session) return;
    items = data.items; $('rows').replaceChildren();
    header(tab === 'audit' ? ['Data','Ação','Tipo','Responsável','Registro']
      : tab === 'users' ? ['Nome','E-mail','Situação','Criado em','']
      : ['Grupo','Proprietário','Situação','Criado em','']);
    for (const item of items) {
      const row = node('tr');
      if (tab === 'audit') {
        cell(row, date(item.createdAt));
        cell(row, actions[item.action] || item.action, 'name');
        cell(row, item.entity === 'User' ? 'Usuário' : 'Grupo');
        cell(row, item.actorName || item.actorAccountId, item.actorName ? '' : 'mono');
        cell(row, item.targetName || item.targetId, item.targetName ? '' : 'mono');
      } else {
        const name = cell(row, '');
        name.append(node('span', 'name', item.name));
        if (item.isSystemAdmin) name.append(badge('Admin', 'accent'));
        if (tab === 'groups' && item.description) name.append(node('span', 'sub', item.description));
        cell(row, tab === 'users' ? item.email : (item.ownerEmail || item.ownerAccountId), tab === 'users' || item.ownerEmail ? '' : 'mono');
        cell(row, '').append(statusBadge(item));
        cell(row, day(item.createdAt));
        const td = cell(row, '', 'actions-cell');
        button(td, 'Detalhes', () => details(item));
        if (item.deletedAt) button(td, 'Restaurar', async () => {
          if (!confirm('Restaurar ' + item.name + '? O registro volta desativado.')) return;
          await api(tab + '/' + item.id + '/restore', {method:'POST', body:JSON.stringify({revision:item.revision})}); await loadAfterMutation('Registro restaurado.');
        });
        else {
          button(td, 'Editar', async () => edit(item));
          button(td, item.isActive ? 'Desativar' : 'Ativar', async () => {
            if (!confirm((item.isActive ? 'Desativar ' : 'Ativar ') + item.name + '?')) return;
            await api(tab + '/' + item.id, {method:'PATCH', body:JSON.stringify({...item, isActive:!item.isActive})});
            await loadAfterMutation(item.isActive ? 'Registro desativado.' : 'Registro ativado.');
          });
          button(td, 'Excluir', async () => {
            if (!confirm('Excluir ' + item.name + '? A exclusão é lógica e o histórico é preservado.')) return;
            await api(tab + '/' + item.id + '?revision=' + item.revision, {method:'DELETE'}); await loadAfterMutation('Registro excluído.');
          }, 'danger');
        }
      }
      $('rows').append(row);
    }
    if (!items.length) { const row = node('tr'); const td = cell(row, 'Nenhum registro encontrado.', 'empty'); td.colSpan = 5; $('rows').append(row); }
    $('pagination').textContent = 'Página ' + data.page + ' de ' + Math.max(1, Math.ceil(data.total / data.pageSize)) + ' · ' + data.total.toLocaleString('pt-BR') + ' registros';
    $('previous').disabled = page <= 1; $('next').disabled = page * data.pageSize >= data.total;
  } finally { loading = false; $('filter').disabled = false; }
}
async function loadAfterMutation(text = 'Alteração salva.') { message(text); await load(); await refreshSummary(); }

async function edit(item) {
  selected = item; $('edit-form').reset(); $('edit-error').textContent = '';
  $('edit-title').textContent = (item ? 'Editar ' : 'Criar ') + (tab === 'users' ? 'usuário' : 'grupo');
  $('user-fields').hidden = tab !== 'users'; $('group-fields').hidden = tab !== 'groups';
  $('email').required = tab === 'users'; $('password').required = tab === 'users' && !item; $('owner').required = tab === 'groups';
  $('name').value = item?.name || ''; $('email').value = item?.email || ''; $('description').value = item?.description || ''; $('owner').value = '';
  if (tab === 'groups' && item) { const detail = await api('groups/' + item.id); $('owner').value = detail.members.find(m => m.id === item.ownerAccountId)?.email || ''; }
  $('active').checked = item ? item.isActive : true; $('system-admin').checked = !!item?.isSystemAdmin;
  $('editor').showModal();
}

// Human-readable values for the detail view.
function format(key, value) {
  if (value === null || value === undefined || value === '') return '—';
  if (typeof value === 'boolean') return value ? 'Sim' : 'Não';
  if (key === 'role' && typeof value === 'number') return roles[value] ?? value;
  if (key === 'recurrence' && typeof value === 'number') return recurrences[value] ?? value;
  if (key === 'status' && typeof value === 'number') return invitationStatus[value] ?? value;
  if (/(At|AtUtc)$/.test(key) && typeof value === 'string') return date(value);
  return String(value);
}
// A group alarm carries its definition nested; show its title, time and recurrence as columns.
const flatten = row => row && row.alarm ? {title:row.alarm.title, scheduledAtUtc:row.alarm.scheduledAtUtc, recurrence:row.alarm.recurrence, version:row.version} : row;

function renderObject(value) {
  const dl = node('dl');
  for (const [key, val] of Object.entries(value)) {
    if (hiddenKeys.has(key)) continue;
    dl.append(node('dt', '', labels[key] || key), node('dd', guid.test(String(val)) ? 'mono' : '', format(key, val)));
  }
  return dl;
}
function renderTable(rows) {
  const data = rows.map(flatten);
  const keys = Object.keys(data[0]).filter(key => !hiddenKeys.has(key) && key !== 'id' && !key.endsWith('AccountId'));
  const table = node('table'), head = node('tr'), body = node('tbody');
  for (const key of keys) head.append(node('th', '', labels[key] || key));
  for (const row of data) { const tr = node('tr'); for (const key of keys) tr.append(node('td', '', format(key, row[key]))); body.append(tr); }
  const thead = node('thead'); thead.append(head); table.append(thead, body);
  const wrap = node('div', 'table-wrap'); wrap.append(table); return wrap;
}

async function details(item) {
  const data = await api(tab + '/' + item.id); const content = $('detail-content'); content.replaceChildren();
  $('details-title').textContent = item.name;
  // The owner is one of the members: show who it is instead of an identifier.
  if (data.group && Array.isArray(data.members)) {
    const owner = data.members.find(m => m.id === data.group.ownerAccountId);
    const {ownerEmail, ...group} = data.group;
    data.group = owner ? {...group, ownerAccountId: owner.name + ' · ' + owner.email} : group;
  }
  for (const [key, value] of Object.entries(data)) {
    content.append(node('h3', '', labels[key] || key));
    if (Array.isArray(value)) content.append(value.length ? renderTable(value) : node('p', 'none', 'Nenhum registro.'));
    else if (value && typeof value === 'object') content.append(renderObject(value));
    else content.append(node('p', '', format(key, value)));
  }
  $('details').showModal();
}

function selectTab(name) {
  tab = name; page = 1; $('search').value = ''; $('status').value = '';
  document.querySelectorAll('[data-tab]').forEach(b => b.setAttribute('aria-selected', String(b.dataset.tab === name)));
  const audit = name === 'audit';
  $('create').hidden = audit; $('search').hidden = audit; $('status').hidden = audit; $('filter').hidden = audit;
  $('create').textContent = name === 'users' ? 'Novo usuário' : 'Novo grupo';
  $('search').placeholder = name === 'groups' ? 'Buscar por nome do grupo' : 'Buscar por nome ou e-mail';
}

$('login-form').onsubmit = async event => {
  event.preventDefault(); message(''); const submit = event.submitter; submit.disabled = true;
  try {
    const r = await fetch('/api/auth/login', {method:'POST', headers:{'Content-Type':'application/json'}, body:JSON.stringify({email:$('login-email').value, password:$('login-password').value})});
    if (!r.ok) throw new Error('E-mail ou senha inválidos, ou conta desativada.');
    session = await r.json(); $('login-password').value = '';
    try { await api('me'); } catch (error) { if (session) await fetch('/api/auth/logout', {method:'POST', headers:{Authorization:'Bearer ' + session.accessToken}}); clearSession(); throw error; }
    $('login').hidden = true; $('panel').hidden = false; $('logout').hidden = false;
    $('who').textContent = session.user.name; $('who').hidden = false;
    selectTab('users'); await refreshSummary(); await load();
  } catch (e) { fail(e); } finally { submit.disabled = false; }
};
$('logout').onclick = async () => {
  const token = session?.accessToken; clearSession();
  if (token) await fetch('/api/auth/logout', {method:'POST', headers:{Authorization:'Bearer ' + token}}).catch(() => {});
  message('Sessão encerrada.');
};
document.querySelectorAll('[data-tab]').forEach(b => b.onclick = async () => {
  if (loading || b.dataset.tab === tab) return;
  selectTab(b.dataset.tab); await load().catch(fail);
});
$('filter-form').onsubmit = event => { event.preventDefault(); page = 1; load().catch(fail); };
$('previous').onclick = () => { if (loading) return; page--; load().catch(fail); };
$('next').onclick = () => { if (loading) return; page++; load().catch(fail); };
$('create').onclick = () => edit(null).catch(fail);
$('cancel-edit').onclick = () => $('editor').close();
$('close-details').onclick = () => $('details').close();
$('edit-form').onsubmit = async event => {
  event.preventDefault(); $('save').disabled = true; $('edit-error').textContent = '';
  const body = {name:$('name').value, isActive:$('active').checked, revision:selected?.revision || 0};
  if (tab === 'users') { body.email = $('email').value; body.isSystemAdmin = $('system-admin').checked; if ($('password').value) body.password = $('password').value; }
  else { body.description = $('description').value; body.ownerAccountId = selected?.ownerAccountId || '00000000-0000-0000-0000-000000000000'; body.ownerEmail = $('owner').value; }
  try {
    await api(tab + (selected ? '/' + selected.id : ''), {method:selected ? 'PATCH' : 'POST', body:JSON.stringify(body)});
    $('password').value = ''; $('editor').close(); await loadAfterMutation(selected ? 'Alterações salvas.' : 'Registro criado.');
  } catch (e) { $('edit-error').textContent = e.message; } finally { $('save').disabled = false; }
};
