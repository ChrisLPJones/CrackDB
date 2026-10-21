const $ = (id) => document.getElementById(id);
const db = () => $('db').value;
const enc = encodeURIComponent;

async function api(path, opts) {
  const r = await fetch(path, { credentials: 'same-origin', ...opts });
  if (r.status === 401) { location.href = '/login.html'; throw new Error('signed out'); }
  const data = await r.json().catch(() => ({}));
  if (!r.ok) throw httpError(r.status, data);
  return data;
}

// Errors carry the parsed problem body (status, code, duplicateOf) so callers can branch on `code`.
function httpError(status, data) {
  const e = new Error(data.detail || data.title || ('Request failed (' + status + ')'));
  e.status = status; e.data = data;
  return e;
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
    show('dberror', '');
    onDbChanged();
  } catch (e) { fail('dberror', e); }
}

function updateExportAction() { $('export').action = '/api/dbs/' + enc(db()) + '/export'; }

// Export file name: the button opens a small prompt pre-filled with the default (follows min/max). The
// name goes to the server in the same `filename` form field; the server re-sanitises whatever is sent.
function defaultExportName() {
  const mn = $('emin').value.trim(), mx = $('emax').value.trim();
  if (mn && mx) return 'wordlist-' + mn + '-' + mx + '.lst';
  if (mn) return 'wordlist-min' + mn + '.lst';
  if (mx) return 'wordlist-max' + mx + '.lst';
  return 'wordlist.lst';
}
$('export').addEventListener('submit', (e) => {
  e.preventDefault();
  $('exportname').value = defaultExportName();
  $('exportdialog').showModal();
  $('exportname').select();
});
$('exportcancel').addEventListener('click', () => { $('exportdialog').close(); });
$('exportnameform').addEventListener('submit', (e) => {
  e.preventDefault();
  $('efile').value = $('exportname').value.trim() || defaultExportName();
  $('exportdialog').close();
  $('export').submit();   // programmatic submit does not re-fire the submit event
});

// Cheap approximate size: refreshed on db selection and when a job finishes (no polling).
async function loadSize() {
  if (!db()) { show('size', ''); return; }
  const wanted = db();
  try {
    const s = await api('/api/dbs/' + enc(wanted) + '/stats?full=false');
    if (db() === wanted) { bdEst = Number(s.estimatedCount) || 0; show('size', fmt(s.estimatedCount) + ' passwords (approximate)'); renderBdButton(); }
  } catch (e) { if (db() === wanted) fail('size', e); }
}

function onDbChanged() {
  updateExportAction();
  resetBreakdown();
  loadSize();
  loadHistory();
}

$('db').addEventListener('change', onDbChanged);

// ---- import history (advisory record of past imports; all values are untrusted text) ----

// DD/MM/YYYY HH:MM (24h, browser local time zone), independent of the browser locale.
const p2 = (n) => String(n).padStart(2, '0');
const fmtDay = (d) => p2(d.getDate()) + '/' + p2(d.getMonth() + 1) + '/' + d.getFullYear();
const fmtDate = (s) => {
  const d = s ? new Date(s) : null;
  return d && !isNaN(d) ? fmtDay(d) + ' ' + p2(d.getHours()) + ':' + p2(d.getMinutes()) : '-';
};

let histEntries = [];
let histSort = { key: 'date', dir: 'desc' };   // default: newest first, as the server returns it
const histName = (r) => (r.source && r.source.startsWith('upload:') ? r.source.slice('upload:'.length) : (r.source || ''));
const histKeys = {
  date: (r) => (r.importedAt ? Date.parse(r.importedAt) || 0 : 0),
  name: (r) => histName(r).toLowerCase(),
  size: (r) => Number(r.sizeBytes) || 0,
  lines: (r) => Number(r.linesRead) || 0,
};
function renderHistory() {
  const k = histKeys[histSort.key];
  const sign = histSort.dir === 'asc' ? 1 : -1;
  const rows = histEntries.map((r, i) => ({ r, i, v: k(r) }));
  rows.sort((x, y) => (x.v < y.v ? -sign : x.v > y.v ? sign : x.i - y.i));
  $('histbody').replaceChildren(...rows.map(({ r }) => {
    const tr = document.createElement('tr');
    const cell = (text, title) => { const td = document.createElement('td'); td.textContent = text; if (title) td.title = title; return td; };
    tr.append(cell(fmtDate(r.importedAt)), cell(histName(r), r.source), cell(fmtBytes(r.sizeBytes)), cell(fmt(r.linesRead)));
    return tr;
  }));
  document.querySelectorAll('#hist th[data-sort]').forEach((th) => {
    const active = th.dataset.sort === histSort.key;
    th.setAttribute('aria-sort', active ? (histSort.dir === 'asc' ? 'ascending' : 'descending') : 'none');
    th.querySelector('.sortind').textContent = active ? (histSort.dir === 'asc' ? '\u25B2' : '\u25BC') : '';
  });
}
document.querySelectorAll('#hist th[data-sort]').forEach((th) => {
  th.querySelector('button').addEventListener('click', () => {
    const key = th.dataset.sort;
    histSort = histSort.key === key ? { key, dir: histSort.dir === 'asc' ? 'desc' : 'asc' }
      : { key, dir: key === 'name' ? 'asc' : 'desc' };
    renderHistory();
  });
});

async function loadHistory() {
  const wanted = db();
  const body = $('histbody');
  if (!wanted) { histEntries = []; body.replaceChildren(); show('histinfo', ''); return; }
  try {
    const h = await api('/api/dbs/' + enc(wanted) + '/imports?limit=5000');
    if (db() !== wanted) return;
    histEntries = h.entries;
    renderHistory();
    show('histinfo', h.total ? (h.total > h.entries.length ? 'Showing the latest ' + h.entries.length + ' of ' + h.total + '. ' : '') +
      'Imports run from the command line are not recorded. Lines read is the count of lines in the file, not new passwords.'
      : 'No imports recorded for this database yet.');
  } catch (e) { if (db() === wanted) { body.replaceChildren(); fail('histinfo', e); } }
}

// ---- length breakdown: the server stores the last exact result (recomputed after the Sunday compaction);
// the button queues a scan job. Nothing carries over between databases. ----

let bdEst = 0;   // approximate entry count of the current db, for scan progress text
let bd = { db: '', loaded: false, hasStored: false, activeId: null, posting: false, note: '' };
const bdOpen = () => document.querySelector('#stats-panel details').open;
const bdJob = () => lastJobs.find((j) => j.kind === 'breakdown' && j.db === db() && !TERMINAL.includes(j.status));

function renderLengths(lengths) {
  const out = $('stats');
  out.textContent = '';
  if (!lengths.length) { out.textContent = 'Database is empty.'; return; }
  const grid = document.createElement('div');
  grid.className = 'lengthgrid';
  // Column-major fill (column 1 = the lowest lengths, column 2 = the next chunk, and so on),
  // evenly split across columns regardless of how many distinct lengths there are.
  const cols = window.matchMedia('(max-width: 480px)').matches ? 2 : 3;
  const rows = Math.max(1, Math.ceil(lengths.length / cols));
  grid.style.gridTemplateRows = `repeat(${rows}, auto)`;
  grid.append(...lengths.map((l) => {
    const cell = document.createElement('div');
    cell.className = 'lengthcell';
    const a = document.createElement('span'); a.className = 'muted'; a.textContent = 'length ' + l.length + ': ';
    const b = document.createElement('span'); b.textContent = fmt(l.count);
    cell.append(a, b); return cell;
  }));
  out.append(grid);
}

function resetBreakdown() {
  bd = { db: db(), loaded: false, hasStored: false, activeId: null, posting: false, note: '' };
  bdEst = 0;
  show('stats', '');
  show('statsasof', '');
  renderBdButton();
  if (db() && bdOpen()) loadBreakdown();
}

async function loadBreakdown() {
  const wanted = db();
  if (!wanted) return;
  try {
    const r = await api('/api/dbs/' + enc(wanted) + '/length-breakdown');
    if (db() !== wanted) return;   // stale response for a database no longer selected
    bd.loaded = true;
    bd.hasStored = r.asOf != null && r.lengths != null;
    const note = bd.note ? ' ' + bd.note : '';
    bd.note = '';
    if (bd.hasStored) {
      renderLengths(r.lengths);
      show('statsasof', 'As of ' + fmtDate(r.asOf) + '.' + note);
    } else {
      show('stats', '');
      show('statsasof', 'Not computed yet. It runs after the next weekly compaction, or press Initialize breakdown.' + note);
    }
  } catch (e) {
    if (db() !== wanted) return;
    bd.loaded = true;   // leave the button usable with the last known label; the click still POSTs
    show('statsasof', e.message === 'signed out' ? '' : e.message);
  }
  renderBdButton();
}

// One place decides the button label and the status line, called after each GET and each jobs poll.
function renderBdButton() {
  const btn = $('statsbtn'), line = $('statsjob');
  const job = bdJob();
  if (job) {
    bd.activeId = job.id;
    btn.disabled = true;
    btn.textContent = job.status === 'queued' ? 'Queued...' : 'Scanning...';
    let t;
    if (job.status === 'queued') {
      const ahead = runningJob();
      t = ahead ? 'Queued, waiting for ' + verbing(ahead) + ' of ' + ahead.db + ' to finish.' : 'Queued.';
    } else {
      t = 'Scanning: ' + fmt(job.scannedCount) + (bdEst > 0 ? ' of about ' + fmt(bdEst) : '') + ' entries';
    }
    $('statsjobtext').textContent = t;
    line.hidden = false;
    return;
  }
  line.hidden = true;
  if (bd.activeId) {
    // The scan we were showing has ended: re-read the stored result (it only changes on success).
    const done = lastJobs.find((j) => j.id === bd.activeId);
    bd.activeId = null;
    if (done && done.status === 'failed' && done.error) bd.note = 'Last scan failed: ' + done.error;
    else if (done && done.status === 'cancelled') bd.note = 'Last scan was cancelled.';
    bd.loaded = false;
    loadBreakdown();
  }
  btn.textContent = bd.hasStored ? 'Update breakdown' : 'Initialize breakdown';
  btn.disabled = !bd.loaded || bd.posting;
}

$('statsbtn').addEventListener('click', async () => {
  if (!db() || bd.posting) return;
  bd.posting = true;
  renderBdButton();
  try {
    await api('/api/jobs/length-breakdown', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ db: db() }) });
    await pollJobs();
  } catch (e) { fail('statsasof', e); }
  finally { bd.posting = false; renderBdButton(); }
});

$('statscancel').addEventListener('click', async () => {
  const job = bdJob();
  if (!job) return;
  $('statscancel').disabled = true;
  try { await api('/api/jobs/' + enc(job.id) + '/cancel', { method: 'POST' }); await pollJobs(); }
  catch (e) { fail('statsasof', e); }
  finally { $('statscancel').disabled = false; }
});

document.querySelector('#stats-panel details').addEventListener('toggle', () => {
  if (bdOpen() && !bd.loaded) loadBreakdown();
});

// ---- import / compact jobs ----

const TERMINAL = ['completed', 'failed', 'cancelled'];
let events = null;
let currentJob = null;
let batch = null;       // { db, queue, index, total, cancelled, xhr } while a multi-file upload runs
let jobWaiter = null;   // { id, resolve } - lets the batch loop await a job's terminal state
// Upload timer: elapsed for the current file and for the whole batch; keeps counting through retry waits.
let timerId = null;
const fmtClock = (ms) => {
  const s = Math.max(0, Math.floor(ms / 1000));
  const p = (n) => String(n).padStart(2, '0');
  return s >= 3600 ? Math.floor(s / 3600) + ':' + p(Math.floor(s / 60) % 60) + ':' + p(s % 60) : p(Math.floor(s / 60)) + ':' + p(s % 60);
};
// Progress is counted in "units": each file is 2 x its size (upload, then import), so one smoothed
// rate (units/s, moving average over the last RATE_WINDOW_MS) covers both phases.
const RATE_WINDOW_MS = 15000;
const RATE_MIN_SPAN_MS = 3000;     // "calculating..." until this much history exists
const MAX_ETA_S = 24 * 3600;       // cap silly estimates
const fmtEta = (s) => (s > MAX_ETA_S ? 'over 24:00:00' : fmtClock(s * 1000));
const curUnits = () => (batch && batch.cur ? batch.cur.up + batch.cur.imp : 0);
function tickTimer() {
  if (!batch) return;
  const now = Date.now();
  let text = 'Elapsed ' + fmtClock(now - batch.fileStart);
  if (batch.paused) {
    batch.samples = [];   // the wait must not drag the rate down
    text += ' \u00b7 paused, waiting for the server';
  } else {
    const u = batch.doneUnits + curUnits();
    const s = batch.samples;
    if (s.length && u < s[s.length - 1].u) s.length = 0;   // a file restarted
    s.push({ t: now, u });
    while (s.length > 2 && now - s[0].t > RATE_WINDOW_MS) s.shift();
    const span = now - s[0].t;
    const rate = span >= RATE_MIN_SPAN_MS ? (u - s[0].u) / (span / 1000) : 0;
    if (!(rate > 0)) text += ' \u00b7 calculating time left...';
    else {
      const batchLeft = (2 * batch.totalBytes - u) / rate;
      text += ' \u00b7 ' + fmtBytes(rate) + '/s \u00b7 about ' + fmtEta(Math.max(0, batchLeft)) + ' left';
    }
  }
  show('jobtimer', text);
}
function stopTimer(finalText) {
  if (timerId) { clearInterval(timerId); timerId = null; }
  show('jobtimer', finalText || '');
}
const batchSuffix = () => (batch ? ' (file ' + batch.index + ' of ' + batch.total + ')' : '');

const post = (path, body) => api(path, {
  method: 'POST',
  headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify(body),
});

$('createtoggle').addEventListener('click', () => {
  show('createerror', '');
  $('newdb').value = '';
  $('createdialog').showModal();
  $('newdb').focus();
});
$('createcancel').addEventListener('click', () => { $('createdialog').close(); });

$('createform').addEventListener('submit', async (ev) => {
  ev.preventDefault();
  const name = $('newdb').value.trim();
  if (!name) { show('createerror', 'Enter a database name.'); return; }
  try {
    await post('/api/dbs', { name });
    show('createerror', '');
    $('newdb').value = '';
    $('createdialog').close();
    await loadDbs(name);
  } catch (e) { fail('createerror', e); }
});

function closeDelete() { $('delconfirm').hidden = true; $('delname').value = ''; }

$('deltoggle').addEventListener('click', () => {
  if (!db()) { show('deleteerror', 'Select a database first.'); return; }
  show('deleteerror', '');
  $('delname').value = '';
  $('delconfirm').hidden = false;
  $('delname').focus();
});
$('delcancel').addEventListener('click', () => { closeDelete(); show('deleteerror', ''); });

$('delbtn').addEventListener('click', async () => {
  const name = db();
  if (!name) { show('deleteerror', 'Select a database first.'); return; }
  if ($('delname').value !== name) { show('deleteerror', 'Type the exact database name to confirm.'); return; }
  const btn = $('delbtn');
  btn.disabled = true;
  try {
    await api('/api/dbs/' + enc(name), { method: 'DELETE' });
    show('deleteerror', '');
    closeDelete();
    show('size', '');
    await loadDbs();
  } catch (e) { fail('deleteerror', e); }
  finally { btn.disabled = false; }
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

// ---- maintenance banner / queue position: polls the (cheap, in-memory) jobs list ----

let lastJobs = [];
const activeJobs = () => lastJobs.filter((j) => !TERMINAL.includes(j.status));
const runningJob = () => lastJobs.find((j) => j.status === 'running');
const verbing = (j) => (j.kind === 'compact' ? 'compaction' : j.kind === 'breakdown' ? 'length scan' : j.kind === 'search' ? 'search' : 'import');

function renderBanner() {
  // Only a running import or compaction takes the database away; a breakdown scan or search leaves reads working.
  const r = lastJobs.find((j) => j.status === 'running' && (j.kind === 'import' || j.kind === 'compact'));
  const b = $('banner');
  const sum = $('importsummary');
  const clearNotice = () => { if (sum.dataset.kind === 'notice') { sum.textContent = ''; sum.className = ''; delete sum.dataset.kind; } };
  if (!r) { b.hidden = true; clearNotice(); return; }
  const waiting = activeJobs().filter((j) => j.status === 'queued').length;
  const queued = waiting ? ' ' + waiting + ' job' + (waiting === 1 ? '' : 's') + ' waiting in the queue.' : '';
  if (r.kind === 'compact') {
    b.textContent = 'Maintenance in progress: database ' + r.db + ' is being compacted. Search and imports are unavailable until it finishes.' + queued;
    b.hidden = false;
    clearNotice();
    return;
  }
  // The import notice lives under the upload output (the same spot as the batch summary), not in the
  // top banner. A finished batch summary wins and is never overwritten.
  b.hidden = true;
  if (sum.dataset.kind === 'summary') return;
  sum.textContent = 'Import in progress: database ' + r.db + ' is being imported. Search of it is unavailable until it finishes.' + queued;
  sum.className = 'hint';
  sum.dataset.kind = 'notice';
}

async function pollJobs() {
  try {
    ({ jobs: lastJobs } = await api('/api/jobs'));
    renderBanner();
    renderBdButton();
    if (currentJob && currentJob.status === 'queued') {
      const mine = lastJobs.find((j) => j.id === currentJob.id);
      if (mine) renderJob(mine);
    }
  } catch (e) { /* transient; try again next tick */ }
}
setInterval(pollJobs, 3000);

function renderJob(j) {
  currentJob = j;
  $('job').hidden = false;
  $('jobtitle').textContent = (j.kind === 'import' ? 'Import into ' : 'Compact ') + j.db + batchSuffix();
  const st = $('jobstatus'); st.textContent = j.status; st.className = 'badge ' + j.status;
  if (batch && batch.cur && j.kind === 'import') {
    batch.cur.up = batch.cur.size;
    if (j.bytesTotal > 0) batch.cur.imp = batch.cur.size * Math.min(1, j.bytesRead / j.bytesTotal);
  }
  const bar = $('jobbar');
  if (j.kind === 'import' && j.bytesTotal > 0) { bar.max = 100; bar.value = Math.min(100, (j.bytesRead / j.bytesTotal) * 100); }
  else if (j.status === 'completed') { bar.max = 100; bar.value = 100; }
  else bar.removeAttribute('value');   // indeterminate
  if (j.status === 'completed' && j.kind === 'import') bar.value = 100;
  const rows = j.kind === 'import' ? [
    ['Source', histName(j) || '-'],
    ['Lines read', fmt(j.linesRead)],
    ['Bytes', fmtBytes(j.bytesRead) + ' / ' + fmtBytes(j.bytesTotal)],
    ['New entries (approx)', fmt(j.estNewEntries)],
    ['Elapsed', fmtElapsed(j)],
  ] : [['Elapsed', fmtElapsed(j)]];
  $('jobstats').replaceChildren(...rows.flatMap(([k, v]) => {
    const dt = document.createElement('dt'); dt.textContent = k;
    const dd = document.createElement('dd'); dd.textContent = v; return [dt, dd];
  }));
  show('joberror', j.error || '');
  show('jobnote', j.note || '');
  const ahead = j.status === 'queued' ? runningJob() : null;
  if (j.status === 'queued') {
    show('jobwait', ahead
      ? 'Queued, waiting for ' + verbing(ahead) + ' of ' + ahead.db + ' to finish.'
      : 'Queued, starting shortly.');
  } else show('jobwait', '');
  const done = TERMINAL.includes(j.status);
  const cancellable = !done && (j.kind === 'import' || j.status === 'queued');
  $('jobcancel').hidden = !cancellable;
  $('jobcancel').textContent = j.status === 'queued' ? 'Remove from queue' : 'Cancel';
  if (done) {
    closeEvents();
    loadDbs();   // also refreshes the size and the history
    if (jobWaiter && jobWaiter.id === j.id) { const w = jobWaiter; jobWaiter = null; w.resolve(j); }
    else if (!batch) setBusy(false);   // a running batch keeps the button disabled until it finishes
  }
}

function closeEvents() { if (events) { events.close(); events = null; } }
// During a batch the file button stays usable: it appends more files to the queue.
function setBusy(b) {
  const off = b && !batch;
  $('ifile').disabled = off;
  $('filebtn').classList.toggle('disabled', off);
  $('filebtn').textContent = batch ? 'Upload to queue' : 'Upload and import';
}

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
    } catch (e) {
      closeEvents();
      // Batch file whose job vanished (server restarted: 502/network error, or 404 as jobs are in memory):
      // report it as interrupted so runBatch waits for the server and re-uploads the file.
      if (jobWaiter && jobWaiter.id === id && (e.status === 404 || isTransient(e))) {
        const w = jobWaiter; jobWaiter = null; currentJob = null;
        show('joberror', '');
        w.resolve({ status: 'interrupted', error: e.message });
        return;
      }
      fail('joberror', e);
      // The job can no longer be followed (e.g. server restart): fail the batch's file so it moves on.
      // Nulling jobWaiter first makes a later real terminal snapshot a no-op (resolves only once).
      if (jobWaiter && jobWaiter.id === id) {
        const w = jobWaiter; jobWaiter = null; currentJob = null;
        w.resolve({ status: 'failed', error: e.message });
      } else if (!batch) setBusy(false);
    }
  };
}

// Upload via XHR (fetch cannot report upload progress). Mirrors api(): 401 -> login, errors use the
// JSON detail message. Resolves with the parsed 202 body ({ id }).
function uploadXhr(fd, onProgress, onXhr) {   // reject(e): e.status / e.data hold the parsed error body
  return new Promise((resolve, reject) => {
    const xhr = new XMLHttpRequest();
    onXhr(xhr);
    xhr.open('POST', '/api/jobs/upload');
    xhr.withCredentials = true;
    xhr.upload.onprogress = (e) => { if (e.lengthComputable) onProgress(e.loaded, e.total); };
    xhr.onload = () => {
      if (xhr.status === 401) { location.href = '/login.html'; reject(new Error('signed out')); return; }
      let data = {};
      try { data = JSON.parse(xhr.responseText); } catch (e) { /* non-JSON body */ }
      if (xhr.status >= 200 && xhr.status < 300) resolve(data);
      else reject(httpError(xhr.status, data));
    };
    xhr.onerror = () => { const e = new Error('Upload failed.'); e.status = 0; reject(e); };   // status 0 = network error (transient)
    xhr.onabort = () => reject(new Error('cancelled'));
    xhr.send(fd);
  });
}

// Phase 1 (browser -> server transfer). File names are untrusted: textContent only.
function renderUpload(name, loaded, total) {
  $('job').hidden = false;
  currentJob = null;
  $('jobtitle').textContent = 'Uploading ' + name + batchSuffix();
  const st = $('jobstatus'); st.textContent = 'uploading'; st.className = 'badge running';
  const pct = total > 0 ? Math.min(100, (loaded / total) * 100) : 0;
  if (batch && batch.cur) { batch.cur.up = batch.cur.size * pct / 100; batch.cur.imp = 0; }
  const bar = $('jobbar'); bar.max = 100; bar.value = pct;
  const dt = document.createElement('dt'); dt.textContent = 'Uploaded';
  const dd = document.createElement('dd');
  dd.textContent = Math.floor(pct) + '% (' + fmtBytes(loaded) + ' / ' + fmtBytes(total) + ')';
  $('jobstats').replaceChildren(dt, dd);
  show('joberror', '');
  show('jobnote', '');
  $('jobcancel').hidden = false;
  $('jobcancel').textContent = 'Cancel';
}

// ---- pre-upload duplicate check ----
// Identical content implies identical size, so a size the database has never imported cannot be a duplicate and
// the file is uploaded at once. Only on a size match is the file hashed here (SHA-256 over 8 MiB slices, never the
// whole file in memory) and the hash asked about. The server's post-upload SHA-256 409 stays the final backstop,
// so every failure in this check just falls back to uploading.
const HASH_SLICE = 8 * 1024 * 1024;
function renderChecking(name, loaded, total) {
  $('job').hidden = false;
  currentJob = null;
  $('jobtitle').textContent = 'Checking ' + name + batchSuffix();
  const st = $('jobstatus'); st.textContent = 'checking'; st.className = 'badge running';
  const pct = total > 0 ? Math.min(100, (loaded / total) * 100) : 100;
  const bar = $('jobbar'); bar.max = 100; bar.value = pct;
  const dt = document.createElement('dt'); dt.textContent = 'Checked';
  const dd = document.createElement('dd');
  dd.textContent = Math.floor(pct) + '% (' + fmtBytes(loaded) + ' / ' + fmtBytes(total) + ')';
  $('jobstats').replaceChildren(dt, dd);
  show('joberror', '');
  show('jobnote', '');
  $('jobcancel').hidden = false;
  $('jobcancel').textContent = 'Cancel';
}

// Resolves with the hex digest, or null when the batch was cancelled meanwhile.
async function hashFile(file) {
  const h = new Sha256();
  for (let o = 0; o < file.size; o += HASH_SLICE) {
    if (batch.cancelled) return null;
    const end = Math.min(o + HASH_SLICE, file.size);
    h.update(new Uint8Array(await file.slice(o, end).arrayBuffer()));
    renderChecking(file.name, end, file.size);
  }
  return batch.cancelled ? null : h.hex();
}

// Returns 'duplicate' (skip the upload), 'cancelled', or 'upload' (also for any error: the server decides).
async function precheck(file) {
  const url = '/api/dbs/' + enc(batch.db) + '/imports/seen';
  const post = (body) => api(url, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) });
  try {
    if (!(await post({ size: file.size })).sizeMatch) return 'upload';
    renderChecking(file.name, 0, file.size);
    const sha256 = await hashFile(file);
    if (sha256 === null) return 'cancelled';
    return (await post({ size: file.size, sha256 })).duplicate ? 'duplicate' : 'upload';
  } catch (e) {
    if (e.message === 'signed out') throw e;
    return batch.cancelled ? 'cancelled' : 'upload';
  }
}

// ---- resilience to a server restart / outage during a batch ----
// Transient = the server (or the proxy in front of it) is briefly unavailable: 502/503/504 or a network
// error (status 0, or fetch's TypeError). Never 4xx (409, 413, 401 ...) or 507: those are real answers.
const MAX_RETRIES = 3;                  // retries per file before it is recorded as failed
const HEALTH_POLL_MS = 3000;            // /healthz poll interval while waiting
const HEALTH_GIVE_UP_MS = 10 * 60 * 1000;   // give up waiting for the server after 10 minutes
function isTransient(e) {
  if (!e || e.message === 'signed out' || e.message === 'cancelled') return false;
  if (e.status === undefined) return e instanceof TypeError;
  return e.status === 0 || e.status === 502 || e.status === 503 || e.status === 504;
}
// Sleep that ends early when the batch is cancelled.
async function sleepUnlessCancelled(ms) {
  for (let t = 0; t < ms && !batch.cancelled; t += 250) await new Promise((r) => setTimeout(r, 250));
}
// Polls GET /healthz until it answers 200. Resolves true when up, false if cancelled; throws after the give-up time.
async function waitForServer(file, attempt) {
  const started = Date.now();
  batch.paused = true;
  $('job').hidden = false;
  $('jobcancel').hidden = false; $('jobcancel').textContent = 'Cancel';
  currentJob = null;
  while (!batch.cancelled) {
    show('jobwait', 'Server unavailable - waiting to retry file ' + file.name + ' (attempt ' + attempt + ' of ' + MAX_RETRIES + ')' + batchSuffix());
    const ctl = new AbortController();
    const timer = setTimeout(() => ctl.abort(), 4000);
    batch.xhr = { abort: () => ctl.abort() };   // lets Cancel interrupt the probe at once
    try {
      const r = await fetch('/healthz', { cache: 'no-store', signal: ctl.signal });
      if (r.ok) { show('jobwait', ''); batch.paused = false; return true; }
    } catch (e) { /* still down */ }
    finally { clearTimeout(timer); batch.xhr = null; }
    if (Date.now() - started > HEALTH_GIVE_UP_MS) {
      show('jobwait', '');
      batch.paused = false;
      throw new Error('server did not come back within ' + Math.round(HEALTH_GIVE_UP_MS / 60000) + ' minutes');
    }
    await sleepUnlessCancelled(HEALTH_POLL_MS);
  }
  show('jobwait', '');
  batch.paused = false;
  return false;
}

// Known limitation: the batch state lives in memory only. A page reload mid-batch re-attaches to the
// active job (see below) but does not resume the files that were not yet sent.
// Files are sent one at a time and each import must finish before the next upload starts, so the
// server's single-upload gate never answers 409 and only one job is ever active.
async function runBatch(dbName, files) {
  batch = { db: dbName, queue: files.slice(), index: 0, total: files.length, cancelled: false, xhr: null, startedAt: Date.now(), fileStart: Date.now(),
    totalBytes: files.reduce((n, f) => n + f.size, 0), doneUnits: 0, cur: { size: 0, up: 0, imp: 0 }, samples: [], paused: false };
  setBusy(true);
  { const s = $('importsummary'); s.textContent = ''; s.className = ''; delete s.dataset.kind; }   // a new batch replaces the old summary or notice
  if (timerId) clearInterval(timerId);
  timerId = setInterval(tickTimer, 1000);
  tickTimer();
  const failures = [];
  const skipped = [];
  let imported = 0;
  const send = (file) => {
    const fd = new FormData();   // order matters: text fields first, the file last
    fd.append('db', dbName);
    fd.append('file', file, file.name);
    return uploadXhr(fd, (l, t) => renderUpload(file.name, l, t || file.size), (x) => { batch.xhr = x; });
  };
  renderQueue();
  const gaveUp = [];   // failed after exhausting the retries on a transient/interrupted failure
  let aborted = null;  // set when the server never came back: the rest of the queue is not attempted
  // One attempt at one file. Resolves normally when the file reached a final outcome (imported, skipped
  // or failed for a real reason); throws a transient / interrupted error so the caller can wait and retry.
  const processFile = async (file) => {
    // Already imported (same size, then same SHA-256, checked before any upload): skipped silently, listed in the summary.
    const pre = await precheck(file);
    if (pre === 'cancelled') return;
    if (pre === 'duplicate') { skipped.push(file.name); return; }
    renderUpload(file.name, 0, file.size);
    // Backstop: the server's SHA-256 check after the upload. A true duplicate answers 409 already_imported,
    // which is skipped silently (no prompt, no force) and listed in the batch summary.
    let res;
    try { res = await send(file); }
    catch (e) {
      if (!(e.status === 409 && e.data && e.data.code === 'already_imported')) throw e;
      batch.xhr = null;
      skipped.push(file.name);
      return;
    }
    const { id } = res;
    batch.xhr = null;
    if (res.note) { show('jobnote', res.note); }
    const finished = new Promise((resolve) => { jobWaiter = { id, resolve }; });
    track(id);
    const j = await finished;
    if (j.status === 'interrupted') { const e = new Error('import interrupted (' + j.error + ')'); e.interrupted = true; throw e; }
    if (j.status === 'completed') imported++;
    else if (!batch.cancelled) failures.push(file.name + ': ' + (j.error || j.status));
  };
  while (batch.queue.length) {
    if (batch.cancelled) break;
    const file = batch.queue.shift();
    batch.index++;
    batch.fileStart = Date.now();
    batch.cur = { size: file.size, up: 0, imp: 0 };
    tickTimer();
    renderQueue();
    for (let retries = 0; ; ) {
      try { await processFile(file); break; }
      catch (e) {
        batch.xhr = null;
        if (e.message === 'signed out') { stopTimer(); batch = null; renderQueue(); setBusy(false); return; }
        if (batch.cancelled) break;
        if (!e.interrupted && !isTransient(e)) { failures.push(file.name + ': ' + e.message); break; }
        if (retries >= MAX_RETRIES) { gaveUp.push(file.name + ': ' + e.message + ', after ' + MAX_RETRIES + ' retries'); break; }
        retries++;
        try { if (!(await waitForServer(file, retries))) break; }   // false = cancelled
        catch (we) { gaveUp.push(file.name + ': ' + we.message); aborted = we.message; break; }
      }
    }
    batch.doneUnits += 2 * file.size;   // finished one way or another: counts as done for the batch estimate
    batch.cur = { size: 0, up: 0, imp: 0 };
    if (aborted) break;
  }
  const b = batch;
  stopTimer('Batch finished in ' + fmtClock(Date.now() - b.startedAt));
  batch = null; jobWaiter = null;
  renderQueue();
  setBusy(false);
  // Clean final state: no live Cancel button, and no stale "Uploading..." panel when the last file
  // never reached the import phase (phase-1 cancel/abort or upload error). A job that reached a
  // terminal state keeps its final panel (renderJob already hid Cancel).
  $('jobcancel').hidden = true;
  show('jobwait', '');
  if (!currentJob) $('job').hidden = true;
  let msg = imported + ' of ' + b.total + ' imported';
  if (skipped.length) msg += '; ' + skipped.length + ' already imported, skipped: ' + skipped.join(', ');
  if (b.cancelled) msg += '; cancelled after file ' + b.index + ' of ' + b.total + ' (remaining skipped)';
  if (failures.length) msg += '; ' + failures.length + ' failed: ' + failures.join('; ');
  if (gaveUp.length) msg += '; ' + gaveUp.length + ' failed after retries (server unavailable): ' + gaveUp.join('; ');
  if (aborted) msg += '; stopped, ' + b.queue.length + ' file' + (b.queue.length === 1 ? '' : 's') + ' not attempted';
  const summary = $('importsummary');
  summary.textContent = b.cancelled ? 'File upload cancelled' : msg;
  // Green only when everything went through; any failure/stop keeps the error colour.
  summary.dataset.kind = 'summary';
  summary.className = (failures.length || gaveUp.length || aborted || b.cancelled) ? 'error' : 'success';
  loadDbs();
}

// Waiting files (not yet started). Names are untrusted: textContent only.
function renderQueue() {
  const ul = $('queuelist');
  const q = batch ? batch.queue : [];
  $('queuebox').hidden = !q.length;
  $('queuehead').textContent = 'Waiting in the queue: ' + q.length + (q.length === 1 ? ' file' : ' files');
  ul.replaceChildren(...q.map((file) => {
    const li = document.createElement('li');
    const name = document.createElement('span'); name.textContent = file.name;
    const rm = document.createElement('button'); rm.type = 'button'; rm.textContent = 'Remove';
    rm.setAttribute('aria-label', 'Remove ' + file.name + ' from the queue');
    rm.addEventListener('click', () => {
      if (!batch) return;
      const i = batch.queue.indexOf(file);
      if (i < 0) return;   // already started
      batch.queue.splice(i, 1); batch.total--; batch.totalBytes -= file.size;
      renderQueue();
    });
    li.append(name, rm);
    return li;
  }));
}

// Choosing files starts the upload/import straight away (one button instead of choose + upload).
$('ifile').addEventListener('change', () => {
  const dbName = db();
  const files = Array.from($('ifile').files);
  $('ifile').value = '';   // so picking the same file again still fires change
  if (!dbName) return show('importerror', 'Select or create a database first.');
  if (!files.length) return;
  if (batch && batch.cancelled) return show('importerror', 'The batch is being cancelled; wait for it to stop.');
  // Simplest safe rule: files added mid-batch go to the running batch's database only.
  if (batch && dbName !== batch.db) return show('importerror', 'Uploads are running into "' + batch.db + '"; select that database to add more files.');
  show('importerror', '');
  if (batch) { batch.queue.push(...files); batch.total += files.length; batch.totalBytes += files.reduce((n, f) => n + f.size, 0); renderQueue(); return; }
  runBatch(dbName, files);
});

$('jobcancel').addEventListener('click', async () => {
  if (batch) batch.cancelled = true;   // skip the remaining files
  if (batch && batch.xhr) { batch.xhr.abort(); return; }   // phase 1: still uploading
  if (!currentJob) return;
  try { renderJob(await api('/api/jobs/' + enc(currentJob.id) + '/cancel', { method: 'POST' })); }
  catch (e) { fail('joberror', e); }
});

// Re-attach to a job that is still active after a page reload.
(async () => {
  try {
    const { jobs } = await api('/api/jobs');
    const active = jobs.find((j) => (j.kind === 'import' || j.kind === 'compact') && !TERMINAL.includes(j.status));   // scans and searches have their own UI
    lastJobs = jobs;
    renderBanner();
    renderBdButton();
    if (active) { renderJob(active); track(active.id); }
  } catch (e) { /* the panel just starts idle */ }
})();

// ---- search: exact = one point read (GET /lookup); Ignore case / Contains = a background scan job ----

const MATCH_PAGE = 5000;
const SEARCH_MIN_CHARS = 4;               // mirrors SearchMatcher.MinChars; the server enforces this regardless
const SEARCH_DISPLAY_CAP = 100000;        // lines shown in the window; "Download all" gets the complete list
let searchEvents = null;
let currentSearchJob = null;
let currentSearchId = null;   // known as soon as the start request returns, even before the first SSE frame
let searchShown = 0;          // matches already appended to the results window
let searchFetching = false;   // one catch-up loop at a time
let searchBusy = false;   // a lookup/scan is in flight: the form is locked until it ends

function closeSearchEvents() { if (searchEvents) { searchEvents.close(); searchEvents = null; } }

function setSearchBusy(busy) {
  searchBusy = busy;
  for (const id of ['squery', 'signore', 'scontains', 'searchbtn']) $(id).disabled = busy;
}

// Matches are fetched in pages after the last one shown and appended as text (never innerHTML), so the
// SSE frames stay small. The window stops at SEARCH_DISPLAY_CAP lines; the rest is only in "Download all".
async function pullMatches(id) {
  if (searchFetching) return;
  searchFetching = true;
  try {
    while (id === currentSearchId && searchShown < SEARCH_DISPLAY_CAP) {
      const page = await api('/api/jobs/' + enc(id) + '/matches?after=' + searchShown + '&limit=' + Math.min(MATCH_PAGE, SEARCH_DISPLAY_CAP - searchShown));
      if (id !== currentSearchId || !page.items.length) break;
      const box = $('searchresults');
      const stick = box.scrollTop + box.clientHeight >= box.scrollHeight - 4;
      box.append(document.createTextNode(page.items.join('\n') + '\n'));
      searchShown = page.next;
      if (stick) box.scrollTop = box.scrollHeight;
    }
  } catch (e) { /* the next frame retries; a 410 just means the results were replaced */ }
  finally { searchFetching = false; }
  if (currentSearchJob) renderSearchNote(currentSearchJob);
}

function renderSearchNote(j) {
  const parts = [];
  if (j.note) parts.push(j.note);
  if (j.matchedCount > SEARCH_DISPLAY_CAP)
    parts.push('Showing the first ' + fmt(SEARCH_DISPLAY_CAP) + ' of ' + fmt(j.matchedCount) + ' matches. Use "Download all" for the complete list.');
  show('searchnote', parts.join(' '));
}

function renderSearchJob(j) {
  currentSearchJob = j;
  currentSearchId = j.id;
  $('searchjob').hidden = false;
  $('searchtitle').textContent = 'Searching ' + j.db + ' for "' + j.source + '"';
  const st = $('searchstatus'); st.textContent = j.status; st.className = 'badge ' + j.status;
  const bar = $('searchbar');
  bar.hidden = false;
  const done = TERMINAL.includes(j.status);
  if (done) { bar.max = 100; bar.value = 100; } else bar.removeAttribute('value');   // indeterminate: no percentage, just a counter below
  $('searchstats').replaceChildren(
    ...[['Scanned', fmt(j.scannedCount) + ' entries'], ['Found', fmt(j.matchedCount)], ['Elapsed', fmtElapsed(j)]]
      .flatMap(([k, v]) => { const dt = document.createElement('dt'); dt.textContent = k; const dd = document.createElement('dd'); dd.textContent = v; return [dt, dd]; }));
  show('searchjoberror', j.error || '');
  if (searchShown < Math.min(j.matchedCount, SEARCH_DISPLAY_CAP)) pullMatches(j.id);
  const n = fmt(j.matchedCount) + (j.matchedCount === 1 ? ' match' : ' matches');
  if (!done) show('searchexact', j.matchedCount ? 'Searching… ' + n + ' so far' : 'Searching…');
  else if (j.status === 'completed') show('searchexact', j.matchedCount ? n : j.source + ' doesn\'t exist in the database');
  else if (j.status === 'cancelled') show('searchexact', 'Cancelled.');
  else show('searchexact', '');
  renderSearchNote(j);
  $('searchcancel').hidden = done;
  const dl = $('searchdownload');
  dl.hidden = !(done && j.status === 'completed' && j.matchedCount > 0);
  if (!dl.hidden) dl.href = '/api/jobs/' + enc(j.id) + '/matches/download';
  if (done) { closeSearchEvents(); setSearchBusy(false); }
}

function trackSearch(id) {
  closeSearchEvents();
  const es = new EventSource('/api/jobs/' + enc(id) + '/events');
  searchEvents = es;
  es.onmessage = (m) => renderSearchJob(JSON.parse(m.data));
  es.onerror = async () => {
    if (es.readyState !== EventSource.CLOSED) return;
    try {
      const j = await api('/api/jobs/' + enc(id));
      renderSearchJob(j);
      if (!TERMINAL.includes(j.status)) setTimeout(() => trackSearch(id), 2000);
    } catch (e) { fail('searchjoberror', e); closeSearchEvents(); setSearchBusy(false); }
  };
}

function resetSearchView(title) {
  closeSearchEvents();
  currentSearchJob = null;
  currentSearchId = null;
  searchShown = 0;
  $('searchresults').textContent = '';
  $('searchjob').hidden = false;
  $('searchtitle').textContent = title;
  show('searchjoberror', ''); show('searchnote', ''); show('searchexact', '');
  $('searchdownload').hidden = true;
}

// A <form> submit covers both the button and Enter in the field (one handler, no double-submit).
$('searchform').addEventListener('submit', async (e) => {
  e.preventDefault();
  if ($('searchbtn').disabled) return;
  const dbName = db();
  const query = $('squery').value;
  const ignoreCase = $('signore').checked, contains = $('scontains').checked;
  show('searcherror', '');
  if (!dbName) { show('searcherror', 'Select a database first.'); return; }
  if (!query) { show('searcherror', 'Enter something to search for.'); return; }
  const scanning = ignoreCase || contains;
  if (scanning && query.length < SEARCH_MIN_CHARS) { show('searcherror', 'Enter at least ' + SEARCH_MIN_CHARS + ' characters.'); return; }
  setSearchBusy(true);
  try {
    if (!scanning) {
      // Exact, case-sensitive: one point read, no job.
      resetSearchView('Looking up in ' + dbName + ': "' + query + '"');
      $('searchstatus').textContent = ''; $('searchstatus').className = 'badge';
      $('searchbar').hidden = true; $('searchstats').replaceChildren(); $('searchcancel').hidden = true;
      const r = await api('/api/dbs/' + enc(dbName) + '/lookup?word=' + enc(query));
      if (r.found) { show('searchexact', '1 match'); $('searchresults').textContent = r.word + '\n'; }
      else show('searchexact', query + ' doesn\'t exist in the database');
      setSearchBusy(false);
      return;
    }
    resetSearchView('Searching ' + dbName + ' for "' + query + '"');
    $('searchcancel').hidden = false;
    show('searchexact', 'Searching…');
    const body = { query, ignoreCase, contains };
    const res = await post('/api/dbs/' + enc(dbName) + '/search', body);
    currentSearchId = res.id;
    trackSearch(res.id);
  } catch (err) { fail('searcherror', err); $('searchjob').hidden = true; setSearchBusy(false); }
});

$('searchcancel').addEventListener('click', async () => {
  if (!currentSearchId) return;
  try { renderSearchJob(await api('/api/jobs/' + enc(currentSearchId) + '/cancel', { method: 'POST' })); }
  catch (e) { fail('searchjoberror', e); }
});

loadDbs();
