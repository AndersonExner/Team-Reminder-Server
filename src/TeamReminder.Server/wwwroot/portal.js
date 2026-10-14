const el = id => document.getElementById(id);
async function loadStats() {
  try {
    const response = await fetch('/api/public/statistics', {cache:'no-store'});
    if (!response.ok) throw new Error();
    const stats = await response.json();
    for (const key of ['accounts','groups','alarms']) el(key).textContent = stats[key].toLocaleString('pt-BR');
    el('api').textContent = '● API online · Banco conectado';
    el('updated').textContent = 'Atualizado em ' + new Date(stats.updatedAt).toLocaleString('pt-BR') + '. Alarmes excluídos não entram na contagem.';
  } catch {
    el('api').textContent = 'Não foi possível verificar o banco';
    for (const key of ['accounts','groups','alarms']) el(key).textContent = '—';
    el('updated').textContent = 'Estatísticas temporariamente indisponíveis.';
  }
}
async function loadRelease() {
  try {
    const response = await fetch('/api/public/release', {cache:'no-store'});
    if (response.status === 404) { el('release').textContent = 'Download em breve — preparando a primeira versão.'; return; }
    if (!response.ok) throw new Error();
    const release = await response.json();
    el('download').hidden = false;
    el('release').textContent = 'Versão ' + release.version + ' · Publicada em ' + new Date(release.publishedAt).toLocaleDateString('pt-BR');
  } catch { el('release').textContent = 'Não foi possível consultar o download. Tente novamente mais tarde.'; }
}
loadStats(); loadRelease();
setInterval(() => { if (!document.hidden) loadStats(); }, 60000);
