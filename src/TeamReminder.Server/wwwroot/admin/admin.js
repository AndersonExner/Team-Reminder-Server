const labels = {user:'Usuário',group:'Grupo',groups:'Grupos',members:'Membros',invitations:'Convites',alarms:'Alarmes (até 100)',alarmCount:'Total de alarmes',id:'Identificador',name:'Nome',email:'E-mail',isActive:'Ativo',isSystemAdmin:'Administrador do sistema',deletedAt:'Excluído em',revision:'Revisão',createdAt:'Criado em',ownerAccountId:'Proprietário',description:'Descrição',role:'Papel',title:'Título',scheduledAtUtc:'Agendado para',version:'Versão sincronizada',recurrence:'Recorrência',expiresAt:'Expira em',status:'Situação',recipientAccountId:'Destinatário'};
const actions = {Created:'Criado',Updated:'Editado',Deleted:'Excluído',Restored:'Restaurado',Bootstrap:'Administrador inicial',PasswordChanged:'Senha alterada',AccessUpdated:'Acesso atualizado',Deactivated:'Desativado'};
const $ = id => document.getElementById(id);
let session = null, tab = 'users', page = 1, items = [], selected = null, loading = false, refreshing = null;
function message(text) { $('message').textContent = text; }
function clearSession() { session = null; $('panel').hidden = true; $('login').hidden = false; $('logout').hidden = true; $('rows').replaceChildren(); $('summary').replaceChildren(); $('detail-content').replaceChildren(); $('details').close(); $('editor').close(); }
async function api(path, options = {}) {
  if (!session) throw new Error('Entre novamente.');
  if (new Date(session.accessExpiresAt).getTime() < Date.now() + 30000) {
    if (!refreshing) refreshing = fetch('/api/auth/refresh', {method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({refreshToken:session.refreshToken})})
      .then(async r => { if (!r.ok) { clearSession(); throw new Error('Sessão expirada. Entre novamente.'); } const data=await r.json(); if(session) session=data; })
      .finally(()=>refreshing=null);
    await refreshing;
  }
  if (!session) throw new Error('Entre novamente.');
  const response = await fetch('/api/admin/' + path, {...options,headers:{'Content-Type':'application/json',Authorization:'Bearer '+session.accessToken},cache:'no-store'});
  if (response.status === 401 || response.status === 403) { clearSession(); throw new Error('Sessão inválida ou sem permissão administrativa.'); }
  if (!response.ok) {
    const body = await response.json().catch(()=>({}));
    throw new Error(body.message || Object.values(body.errors || {}).flat().join(' ') || 'Não foi possível concluir a operação.');
  }
  return response.status === 204 ? null : response.json();
}
function cell(row, text) { const td=document.createElement('td'); td.textContent=text ?? '—'; row.append(td); return td; }
function action(td, title, handler) { const button=document.createElement('button'); button.textContent=title; button.onclick=()=>handler().catch(e=>message(e.message)); td.append(button); }
async function refreshSummary() {
  const data=await api('summary'); $('summary').replaceChildren();
  const labels={accounts:'Contas cadastradas',activeUsers:'Usuários ativos',groups:'Grupos cadastrados',activeGroups:'Grupos ativos',alarms:'Alarmes no servidor',pendingInvitations:'Convites pendentes'};
  for (const [key,label] of Object.entries(labels)) { const box=document.createElement('article'),value=document.createElement('strong'),title=document.createElement('span');value.textContent=data[key].toLocaleString('pt-BR');title.textContent=label;box.append(value,title);$('summary').append(box); }
}
async function load() {
  if(loading) return; loading=true; $('filter').disabled=true;
  try {
    const data=await api(tab+'?'+new URLSearchParams({page,q:$('search').value,status:$('status').value}));
    if(!session) return;
    items=data.items; $('rows').replaceChildren(); $('head').replaceChildren();
    const head=document.createElement('tr');
    for(const name of tab==='audit'?['Data','Ação','Entidade','Responsável / Registro']:['Nome','E-mail / Proprietário','Situação','Ações']){const th=document.createElement('th');th.textContent=name;head.append(th);}
    $('head').append(head);
    for(const item of items){
      const row=document.createElement('tr');
      if(tab==='audit'){cell(row,new Date(item.createdAt).toLocaleString('pt-BR'));cell(row,actions[item.action]||item.action);cell(row,item.entity==='User'?'Usuário':'Grupo');cell(row,(item.actorName||item.actorAccountId)+' / '+(item.targetName||item.targetId));}
      else {
        cell(row,item.name);cell(row,tab==='users'?item.email:(item.ownerEmail||item.ownerAccountId));
        cell(row,item.deletedAt?'Excluído':item.isActive?'Ativo':'Desativado');
        const td=cell(row,'');
        action(td,'Detalhes',()=>details(item));
        if(item.deletedAt)action(td,'Restaurar',async()=>{if(!confirm('Restaurar este registro como desativado?'))return;await api(tab+'/'+item.id+'/restore',{method:'POST',body:JSON.stringify({revision:item.revision})});await loadAfterMutation();});
        else{
          action(td,'Editar',async()=>edit(item));
          action(td,item.isActive?'Desativar':'Ativar',async()=>{
            if(!confirm((item.isActive?'Desativar':'Ativar')+' '+item.name+'?'))return;
            await api(tab+'/'+item.id,{method:'PATCH',body:JSON.stringify({...item,isActive:!item.isActive})});await loadAfterMutation();
          });
          action(td,'Excluir',async()=>{if(!confirm('Excluir logicamente '+item.name+'? O histórico será preservado.'))return;await api(tab+'/'+item.id+'?revision='+item.revision,{method:'DELETE'});await loadAfterMutation();});
        }
      }
      $('rows').append(row);
    }
    if(!items.length){const row=document.createElement('tr');const td=cell(row,'Nenhum registro encontrado.');td.colSpan=4;$('rows').append(row);}
    $('pagination').textContent='Página '+data.page+' · '+data.total+' registros';$('previous').disabled=page<=1;$('next').disabled=page*data.pageSize>=data.total;
  } finally {loading=false;$('filter').disabled=false;}
}
async function loadAfterMutation(){message('Alteração salva.');await load();await refreshSummary();}
async function edit(item) {
  selected=item; $('edit-form').reset();$('edit-error').textContent='';
  $('edit-title').textContent=(item?'Editar ':'Criar ')+(tab==='users'?'usuário':'grupo');
  $('user-fields').hidden=tab!=='users';$('group-fields').hidden=tab!=='groups';
  $('email').required=tab==='users';$('password').required=tab==='users'&&!item;$('owner').required=tab==='groups';
  $('name').value=item?.name||'';$('email').value=item?.email||'';$('description').value=item?.description||'';$('owner').value='';
  if(tab==='groups'&&item){const detail=await api('groups/'+item.id);$('owner').value=detail.members.find(m=>m.id===item.ownerAccountId)?.email||'';}
  $('active').checked=item?item.isActive:true;$('system-admin').checked=!!item?.isSystemAdmin;
  $('editor').showModal();
}
async function details(item) {
  const data=await api(tab+'/'+item.id);$('detail-content').replaceChildren();
  function section(title,value){const h=document.createElement('h3');h.textContent=labels[title]||title;$('detail-content').append(h);
    if(Array.isArray(value)){if(!value.length){const p=document.createElement('p');p.textContent='Nenhum registro.';$('detail-content').append(p);}for(const row of value)section('',row);return;}
    if(value&&typeof value==='object'){const dl=document.createElement('dl');for(const [key,val]of Object.entries(value)){const dt=document.createElement('dt'),dd=document.createElement('dd');dt.textContent=labels[key]||key;dd.textContent=String(val??'—');dl.append(dt,dd);}$('detail-content').append(dl);}
    else{const p=document.createElement('p');p.textContent=String(value??'—');$('detail-content').append(p);}
  }
  for(const [key,value]of Object.entries(data))section(key,value);
  $('details').showModal();
}
$('login-form').onsubmit=async event=>{
  event.preventDefault();message('');const button=event.submitter;button.disabled=true;
  try{
    const r=await fetch('/api/auth/login',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({email:$('login-email').value,password:$('login-password').value})});
    if(!r.ok)throw new Error('E-mail ou senha inválidos, ou conta desativada.');
    session=await r.json();$('login-password').value='';
    try{await api('me');}catch(error){if(session)await fetch('/api/auth/logout',{method:'POST',headers:{Authorization:'Bearer '+session.accessToken}});clearSession();throw error;}
    $('login').hidden=true;$('panel').hidden=false;$('logout').hidden=false;
    await refreshSummary();await load();
  }catch(e){message(e.message);}finally{button.disabled=false;}
};
$('logout').onclick=async()=>{const token=session?.accessToken;clearSession();if(token)await fetch('/api/auth/logout',{method:'POST',headers:{Authorization:'Bearer '+token}}).catch(()=>{});message('Sessão encerrada.');};
document.querySelectorAll('[data-tab]').forEach(button=>button.onclick=async()=>{
  if(loading)return;tab=button.dataset.tab;page=1;$('search').value='';$('status').value='';
  $('create').hidden=tab==='audit';$('search').hidden=tab==='audit';$('status').hidden=tab==='audit';$('create').textContent=tab==='users'?'Novo usuário':'Novo grupo';
  await load().catch(e=>message(e.message));
});
$('filter').onclick=()=>{page=1;load().catch(e=>message(e.message));};
$('previous').onclick=()=>{if(loading)return;page--;load().catch(e=>message(e.message));};
$('next').onclick=()=>{if(loading)return;page++;load().catch(e=>message(e.message));};
$('create').onclick=()=>edit(null).catch(e=>message(e.message));$('cancel-edit').onclick=()=>$('editor').close();$('close-details').onclick=()=>$('details').close();
$('edit-form').onsubmit=async event=>{
  event.preventDefault();$('save').disabled=true;$('edit-error').textContent='';
  const body={name:$('name').value,isActive:$('active').checked,revision:selected?.revision||0};
  if(tab==='users'){body.email=$('email').value;body.isSystemAdmin=$('system-admin').checked;if($('password').value)body.password=$('password').value;}
  else{body.description=$('description').value;body.ownerAccountId=selected?.ownerAccountId||'00000000-0000-0000-0000-000000000000';body.ownerEmail=$('owner').value;}
  try{await api(tab+(selected?'/'+selected.id:''),{method:selected?'PATCH':'POST',body:JSON.stringify(body)});$('password').value='';$('editor').close();await loadAfterMutation();}
  catch(e){$('edit-error').textContent=e.message;}finally{$('save').disabled=false;}
};
