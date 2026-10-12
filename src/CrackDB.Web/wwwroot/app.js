const $ = (id) => document.getElementById(id);
const db = () => $('db').value;
const enc = encodeURIComponent;

async function api(path) {
  const r = await fetch(path, { credentials: 'same-origin' });
  if (r.status === 401) { location.href = '/login.html'; throw new Error('signed out'); }
  const data = await r.json().catch(() => ({}));
  if (!r.ok) throw new Error(data.detail || data.title || ('Request failed (' + r.status + ')'));
  return data;
}

function show(id, text) { $(id).textContent = text; }
function fail(id, e) { show(id, e.message === 'signed out' ? '' : e.message); }
const fmt = (n) => Number(n).toLocaleString();

async function loadDbs() {
  try {
    const { databases } = await api('/api/dbs');
    const sel = $('db');
    sel.replaceChildren(...databases.map((n) => new Option(n, n)));
    show('dberror', databases.length ? '' : 'No databases found.');
    updateExportAction();
  } catch (e) { fail('dberror', e); }
}

function updateExportAction() { $('export').action = '/api/dbs/' + enc(db()) + '/export'; }

$('db').addEventListener('change', updateExportAction);

$('statsbtn').addEventListener('click', async () => {
  show('stats', 'Loading...');
  try {
    const s = await api('/api/dbs/' + enc(db()) + '/stats?full=' + $('full').checked);
    const out = $('stats');
    out.textContent = '~' + fmt(s.estimatedCount) + ' passwords (approximate)';
    if (s.lengths) {
      const t = document.createElement('table');
      t.append(...s.lengths.map((l) => {
        const tr = document.createElement('tr');
        const a = document.createElement('td'); a.textContent = 'length ' + l.length;
        const b = document.createElement('td'); b.textContent = fmt(l.count);
        tr.append(a, b); return tr;
      }));
      out.append(t);
    }
  } catch (e) { fail('stats', e); }
});

$('lookupbtn').addEventListener('click', async () => {
  try {
    const r = await api('/api/dbs/' + enc(db()) + '/lookup?word=' + enc($('word').value));
    show('lookup', r.found ? 'FOUND: in the database.' : 'NOT FOUND.');
  } catch (e) { fail('lookup', e); }
});

$('countbtn').addEventListener('click', async () => {
  const q = new URLSearchParams();
  if ($('cmin').value !== '') q.set('min', $('cmin').value);
  if ($('cmax').value !== '') q.set('max', $('cmax').value);
  show('count', 'Counting...');
  try {
    const r = await api('/api/dbs/' + enc(db()) + '/count?' + q);
    show('count', (r.exact ? '' : '~') + fmt(r.count) + (r.exact ? ' passwords' : ' passwords (approximate total)'));
  } catch (e) { fail('count', e); }
});

$('logout').addEventListener('click', async () => {
  await fetch('/logout', { method: 'POST', credentials: 'same-origin' });
  location.href = '/login.html';
});

loadDbs();
