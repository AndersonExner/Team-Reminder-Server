const el = id => document.getElementById(id);

function apiStatus(ok, text) {
  el('api').classList.toggle('ok', ok);
  el('api').classList.toggle('down', !ok);
  el('api-text').textContent = text;
}

async function loadStats() {
  try {
    const response = await fetch('/api/public/statistics', {cache: 'no-store'});
    if (!response.ok) throw new Error();
    const stats = await response.json();
    for (const key of ['accounts', 'groups', 'alarms']) el(key).textContent = stats[key].toLocaleString('pt-BR');
    apiStatus(true, 'Servidor online');
    el('updated').textContent = 'Atualizado em ' + new Date(stats.updatedAt).toLocaleString('pt-BR') + '. Alarmes excluídos não entram na contagem.';
  } catch {
    apiStatus(false, 'Servidor indisponível');
    for (const key of ['accounts', 'groups', 'alarms']) el(key).textContent = '—';
    el('updated').textContent = 'Estatísticas temporariamente indisponíveis. Os alarmes continuam tocando nos computadores.';
  }
}

async function loadRelease() {
  try {
    const response = await fetch('/api/public/release', {cache: 'no-store'});
    if (response.status === 404) { el('release').textContent = 'Download em breve: preparando a primeira versão.'; return; }
    if (!response.ok) throw new Error();
    const release = await response.json();
    el('download').hidden = false;
    el('release').textContent = 'Versão ' + release.version.replace(/^v/, '') + ' · Publicada em ' + new Date(release.publishedAt).toLocaleDateString('pt-BR');
    if (release.sha256) {
      el('sha').textContent = release.sha256;
      el('checksum').hidden = false;
    }
  } catch { el('release').textContent = 'Não foi possível consultar o download. Tente novamente mais tarde.'; }
}

el('copy-sha').addEventListener('click', async () => {
  try {
    await navigator.clipboard.writeText(el('sha').textContent);
    el('copy-sha').textContent = 'Copiado';
    setTimeout(() => { el('copy-sha').textContent = 'Copiar'; }, 2000);
  } catch { el('copy-sha').textContent = 'Selecione e copie'; }
});

loadStats(); loadRelease();
setInterval(() => { if (!document.hidden) loadStats(); }, 60000);
