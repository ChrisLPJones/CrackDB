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

async function loadDbs() {
  try {
    const { databases } = await api('/api/dbs');
    const sel = $('db');
    sel.replaceChildren(...databases.map((n) => new Option(n, n)));
    $('idb').replaceChildren(new Option('(use the new name below)', ''), ...databases.map((n) => new Option(n, n)));
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

// ---- import / compact jobs ----

const TERMINAL = ['completed', 'failed', 'cancelled'];
let events = null;
let currentJob = null;

const post = (path, body) => api(path, {
  method: 'POST',
  headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify(body),
});

function importTarget() { return $('inew').value.trim() || $('idb').value; }
function intOrNull(id) { return $(id).value === '' ? null : Number($(id).value); }

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
    loadDbs();
    setBusy(false);
  }
}

function closeEvents() { if (events) { events.close(); events = null; } }
function setBusy(b) { for (const id of ['importbtn', 'uploadbtn', 'compactbtn']) $(id).disabled = b; }

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

$('importbtn').addEventListener('click', () => {
  const dbName = importTarget();
  if (!dbName) return show('importerror', 'Choose a database or enter a new name.');
  if (!$('isrc').value.trim()) return show('importerror', 'Enter the server file path.');
  start(post('/api/jobs/import', { db: dbName, source: $('isrc').value.trim(), min: intOrNull('imin'), max: intOrNull('imax') }));
});

$('uploadbtn').addEventListener('click', () => {
  const dbName = importTarget();
  const file = $('ifile').files[0];
  if (!dbName) return show('importerror', 'Choose a database or enter a new name.');
  if (!file) return show('importerror', 'Choose a file to upload.');
  const fd = new FormData();   // order matters: text fields first, the file last
  fd.append('db', dbName);
  if ($('imin').value !== '') fd.append('min', $('imin').value);
  if ($('imax').value !== '') fd.append('max', $('imax').value);
  fd.append('file', file, file.name);
  show('importerror', 'Uploading...');
  start(api('/api/jobs/upload', { method: 'POST', body: fd }));
});

$('compactbtn').addEventListener('click', () => {
  if (!db()) return show('importerror', 'Select a database to compact.');
  start(post('/api/jobs/compact', { db: db() }));
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
