const $ = (id) => document.getElementById(id);
const db = () => $('db').value;
const enc = encodeURIComponent;

async function api(path, opts) {
  const r = await fetch(path, { credentials: 'same-origin', ...opts });
  if (r.status === 401) { location.href = '/login.html'; throw new Error('signed out'); }
  const data = await r.json().catch(() => ({}));
  if (!r.ok) throw new Error(data.detail || data.title || ('Request failed (' + r.status + ')'));
  return data;
}

function show(id, text) { $(id).textContent = text; }
function fail(id, e) { show(id, e.message === 'signed out' ? '' : e.message); }
const fmt = (n) => Number(n).toLocaleString();

async function loadDbs(selectName) {
  try {
    const { databases } = await api('/api/dbs');
    const sel = $('db');
    const keep = sel.value;
    sel.replaceChildren(...databases.map((n) => new Option(n, n)));
    if (selectName && databases.includes(selectName)) sel.value = selectName;
    else if (databases.includes(keep)) sel.value = keep;
    show('dberror', databases.length ? '' : 'No databases found.');
    onDbChanged();
  } catch (e) { fail('dberror', e); }
}

function updateExportAction() { $('export').action = '/api/dbs/' + enc(db()) + '/export'; }

// Cheap approximate size: refreshed on db selection and when a job finishes (no polling).
async function loadSize() {
  if (!db()) { show('size', ''); return; }
  const wanted = db();
  try {
    const s = await api('/api/dbs/' + enc(wanted) + '/stats?full=false');
    if (db() === wanted) show('size', '~' + fmt(s.estimatedCount) + ' passwords (approximate)');
  } catch (e) { if (db() === wanted) fail('size', e); }
}

function onDbChanged() {
  updateExportAction();
  show('stats', '');
  loadSize();
}

$('db').addEventListener('change', onDbChanged);

$('statsbtn').addEventListener('click', async () => {
  if (!db()) return;
  show('stats', 'Loading...');
  try {
    const s = await api('/api/dbs/' + enc(db()) + '/stats?full=true');
    const out = $('stats');
    out.textContent = '';
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

// ---- import / compact jobs ----

const TERMINAL = ['completed', 'failed', 'cancelled'];
let events = null;
let currentJob = null;

const post = (path, body) => api(path, {
  method: 'POST',
  headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify(body),
});

$('createbtn').addEventListener('click', async () => {
  const name = $('newdb').value.trim();
  if (!name) { show('createerror', 'Enter a database name.'); return; }
  try {
    await post('/api/dbs', { name });
    show('createerror', '');
    $('newdb').value = '';
    await loadDbs(name);
  } catch (e) { fail('createerror', e); }
});

function fmtBytes(n) {
  const u = ['B', 'KB', 'MB', 'GB', 'TB']; let i = 0; n = Number(n);
  while (n >= 1024 && i < u.length - 1) { n /= 1024; i++; }
  return n.toFixed(i ? 1 : 0) + ' ' + u[i];
}
function fmtElapsed(j) {
  if (!j.startedAt) return '-';
  const s = Math.max(0, Math.round(((j.endedAt ? new Date(j.endedAt) : new Date()) - new Date(j.startedAt)) / 1000));
  return Math.floor(s / 3600) + 'h ' + String(Math.floor(s / 60) % 60).padStart(2, '0') + 'm ' + String(s % 60).padStart(2, '0') + 's';
}

function renderJob(j) {
  currentJob = j;
  $('job').hidden = false;
  $('jobtitle').textContent = (j.kind === 'import' ? 'Import into ' : 'Compact ') + j.db;
  const st = $('jobstatus'); st.textContent = j.status; st.className = 'badge ' + j.status;
  const bar = $('jobbar');
  if (j.kind === 'import' && j.bytesTotal > 0) { bar.max = 100; bar.value = Math.min(100, (j.bytesRead / j.bytesTotal) * 100); }
  else if (j.status === 'completed') { bar.max = 100; bar.value = 100; }
  else bar.removeAttribute('value');   // indeterminate
  if (j.status === 'completed' && j.kind === 'import') bar.value = 100;
  const rows = j.kind === 'import' ? [
    ['Source', j.source || '-'],
    ['Lines read', fmt(j.linesRead)],
    ['Bytes', fmtBytes(j.bytesRead) + ' / ' + fmtBytes(j.bytesTotal)],
    ['New entries (approx)', '~' + fmt(j.estNewEntries)],
    ['Elapsed', fmtElapsed(j)],
  ] : [['Elapsed', fmtElapsed(j)]];
  $('jobstats').replaceChildren(...rows.flatMap(([k, v]) => {
    const dt = document.createElement('dt'); dt.textContent = k;
    const dd = document.createElement('dd'); dd.textContent = v; return [dt, dd];
  }));
  show('joberror', j.error || '');
  const done = TERMINAL.includes(j.status);
  const cancellable = !done && (j.kind === 'import' || j.status === 'queued');
  $('jobcancel').hidden = !cancellable;
  $('jobcancel').textContent = j.status === 'queued' ? 'Remove from queue' : 'Cancel';
  if (done) {
    closeEvents();
    loadDbs();   // also refreshes the size
    setBusy(false);
  }
}

function closeEvents() { if (events) { events.close(); events = null; } }
function setBusy(b) { $('uploadbtn').disabled = b; }

function track(id) {
  closeEvents();
  setBusy(true);
  const es = new EventSource('/api/jobs/' + enc(id) + '/events');
  events = es;
  es.onmessage = (m) => renderJob(JSON.parse(m.data));
  es.onerror = async () => {
    // EventSource retries by itself; if the connection is gone for good, fall back to one poll.
    if (es.readyState !== EventSource.CLOSED) return;
    try {
      const j = await api('/api/jobs/' + enc(id));
      renderJob(j);
      if (!TERMINAL.includes(j.status)) setTimeout(() => track(id), 2000);
    } catch (e) { fail('joberror', e); setBusy(false); }
  };
}

async function start(promise) {
  show('importerror', '');
  try { track((await promise).id); }
  catch (e) { fail('importerror', e); }
}

$('uploadbtn').addEventListener('click', () => {
  const dbName = db();
  const file = $('ifile').files[0];
  if (!dbName) return show('importerror', 'Select or create a database first.');
  if (!file) return show('importerror', 'Choose a file to upload.');
  const fd = new FormData();   // order matters: text fields first, the file last
  fd.append('db', dbName);
  fd.append('file', file, file.name);
  show('importerror', 'Uploading...');
  start(api('/api/jobs/upload', { method: 'POST', body: fd }));
});

$('jobcancel').addEventListener('click', async () => {
  if (!currentJob) return;
  try { renderJob(await api('/api/jobs/' + enc(currentJob.id) + '/cancel', { method: 'POST' })); }
  catch (e) { fail('joberror', e); }
});

// Re-attach to a job that is still active after a page reload.
(async () => {
  try {
    const { jobs } = await api('/api/jobs');
    const active = jobs.find((j) => !TERMINAL.includes(j.status));
    if (active) { renderJob(active); track(active.id); }
  } catch (e) { /* the panel just starts idle */ }
})();

$('logout').addEventListener('click', async () => {
  await fetch('/logout', { method: 'POST', credentials: 'same-origin' });
  location.href = '/login.html';
});

loadDbs();
