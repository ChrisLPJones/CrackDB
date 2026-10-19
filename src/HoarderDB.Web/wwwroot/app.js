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
  loadHistory();
}

$('db').addEventListener('change', onDbChanged);

// ---- import history (advisory record of past imports; all values are untrusted text) ----

const fmtDate = (s) => (s ? new Date(s).toLocaleString() : '-');

async function loadHistory() {
  const wanted = db();
  const body = $('histbody');
  if (!wanted) { body.replaceChildren(); show('histinfo', ''); return; }
  try {
    const h = await api('/api/dbs/' + enc(wanted) + '/imports?limit=50');
    if (db() !== wanted) return;
    body.replaceChildren(...h.entries.map((r) => {
      const tr = document.createElement('tr');
      const cell = (text, title) => { const td = document.createElement('td'); td.textContent = text; if (title) td.title = title; return td; };
      const name = r.source && r.source.startsWith('upload:') ? r.source.slice('upload:'.length) : r.source;
      tr.append(cell(fmtDate(r.importedAt)), cell(name, r.source), cell(fmtBytes(r.sizeBytes)), cell(fmt(r.linesRead)));
      return tr;
    }));
    show('histinfo', h.total ? (h.total > h.entries.length ? 'Showing the latest ' + h.entries.length + ' of ' + h.total + '. ' : '') +
      'Imports run from the command line are not recorded. Lines read is the count of lines in the file, not new passwords.'
      : 'No imports recorded for this database yet.');
  } catch (e) { if (db() === wanted) { body.replaceChildren(); fail('histinfo', e); } }
}

$('statsbtn').addEventListener('click', async () => {
  if (!db()) return;
  show('stats', 'Loading...');
  try {
    const s = await api('/api/dbs/' + enc(db()) + '/stats?full=true');
    const out = $('stats');
    out.textContent = '';
    if (s.lengths) {
      const grid = document.createElement('div');
      grid.className = 'lengthgrid';
      // Column-major fill (column 1 = the lowest lengths, column 2 = the next chunk, and so on),
      // evenly split across columns regardless of how many distinct lengths there are.
      const cols = window.matchMedia('(max-width: 480px)').matches ? 2 : 3;
      const rows = Math.max(1, Math.ceil(s.lengths.length / cols));
      grid.style.gridTemplateRows = `repeat(${rows}, auto)`;
      grid.append(...s.lengths.map((l) => {
        const cell = document.createElement('div');
        cell.className = 'lengthcell';
        const a = document.createElement('span'); a.className = 'muted'; a.textContent = 'length ' + l.length + ': ';
        const b = document.createElement('span'); b.textContent = fmt(l.count);
        cell.append(a, b); return cell;
      }));
      out.append(grid);
    }
  } catch (e) { fail('stats', e); }
});

// ---- import / compact jobs ----

const TERMINAL = ['completed', 'failed', 'cancelled'];
let events = null;
let currentJob = null;
let batch = null;       // { db, queue, index, total, cancelled, xhr } while a multi-file upload runs
let jobWaiter = null;   // { id, resolve } - lets the batch loop await a job's terminal state
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
    show('stats', '');
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
const verbing = (j) => (j.kind === 'compact' ? 'compaction' : 'import');

function renderBanner() {
  const r = runningJob();
  const b = $('banner');
  if (!r) { b.hidden = true; return; }
  b.textContent = r.kind === 'compact'
    ? 'Maintenance in progress: database ' + r.db + ' is being compacted. Search and imports are unavailable until it finishes.'
    : 'Import in progress: database ' + r.db + ' is being imported. Search of it is unavailable until it finishes.';
  const waiting = activeJobs().filter((j) => j.status === 'queued').length;
  if (waiting) b.textContent += ' ' + waiting + ' job' + (waiting === 1 ? '' : 's') + ' waiting in the queue.';
  b.hidden = false;
}

async function pollJobs() {
  try {
    ({ jobs: lastJobs } = await api('/api/jobs'));
    renderBanner();
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
// During a batch the Upload button stays usable: it appends more files to the queue.
function setBusy(b) { $('uploadbtn').disabled = b && !batch; }

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
      fail('joberror', e);
      closeEvents();
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
    xhr.onerror = () => reject(new Error('Upload failed.'));
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

// Known limitation: the batch state lives in memory only. A page reload mid-batch re-attaches to the
// active job (see below) but does not resume the files that were not yet sent.
// Files are sent one at a time and each import must finish before the next upload starts, so the
// server's single-upload gate never answers 409 and only one job is ever active.
async function runBatch(dbName, files) {
  batch = { db: dbName, queue: files.slice(), index: 0, total: files.length, cancelled: false, xhr: null };
  setBusy(true);
  const failures = [];
  const skipped = [];
  let imported = 0;
  const when = (m) => (m && m.importedAt ? new Date(m.importedAt).toLocaleDateString() : 'earlier');
  const send = (file, force) => {
    const fd = new FormData();   // order matters: text fields first, the file last
    fd.append('db', dbName);
    if (force) fd.append('force', 'true');
    fd.append('file', file, file.name);
    return uploadXhr(fd, (l, t) => renderUpload(file.name, l, t || file.size), (x) => { batch.xhr = x; });
  };
  renderQueue();
  while (batch.queue.length) {
    if (batch.cancelled) break;
    const file = batch.queue.shift();
    batch.index++;
    renderQueue();
    renderUpload(file.name, 0, file.size);
    try {
      // Advisory pre-check by name and size: saves a pointless transfer; the hash check after the upload is authoritative.
      let force = false;
      try {
        const chk = await post('/api/dbs/' + enc(dbName) + '/imports/check', { name: file.name, size: file.size });
        if (chk.duplicate) {
          if (!confirm('"' + file.name + '" looks already imported on ' + when(chk.duplicate) + '. Upload anyway?')) { skipped.push(file.name); continue; }
          force = true;
        }
      } catch (e) { if (e.message === 'signed out') throw e; /* the check is only a hint */ }
      let res;
      try { res = await send(file, force); }
      catch (e) {
        if (!(e.status === 409 && e.data && e.data.code === 'already_imported')) throw e;
        batch.xhr = null;
        if (!confirm('"' + file.name + '" has identical content to an import on ' + when(e.data.duplicateOf) + '. Import it again anyway?')) { skipped.push(file.name); continue; }
        renderUpload(file.name, 0, file.size);
        res = await send(file, true);
      }
      const { id } = res;
      batch.xhr = null;
      if (res.note) { show('jobnote', res.note); }
      const finished = new Promise((resolve) => { jobWaiter = { id, resolve }; });
      track(id);
      const j = await finished;
      if (j.status === 'completed') imported++;
      else if (!batch.cancelled) failures.push(file.name + ': ' + (j.error || j.status));
    } catch (e) {
      batch.xhr = null;
      if (e.message === 'signed out') { batch = null; renderQueue(); setBusy(false); return; }
      if (!batch.cancelled) failures.push(file.name + ': ' + e.message);
    }
  }
  const b = batch;
  batch = null; jobWaiter = null;
  renderQueue();
  setBusy(false);
  // Clean final state: no live Cancel button, and no stale "Uploading..." panel when the last file
  // never reached the import phase (phase-1 cancel/abort or upload error). A job that reached a
  // terminal state keeps its final panel (renderJob already hid Cancel).
  $('jobcancel').hidden = true;
  if (!currentJob) $('job').hidden = true;
  let msg = imported + ' of ' + b.total + ' imported';
  if (skipped.length) msg += '; ' + skipped.length + ' skipped (already imported): ' + skipped.join(', ');
  if (b.cancelled) msg += '; cancelled after file ' + b.index + ' of ' + b.total + ' (remaining skipped)';
  if (failures.length) msg += '; ' + failures.length + ' failed: ' + failures.join('; ');
  show('importerror', msg);
  loadDbs();
}

// Waiting files (not yet started). Names are untrusted: textContent only.
function renderQueue() {
  const ul = $('queuelist');
  const q = batch ? batch.queue : [];
  $('queuebox').hidden = !q.length;
  ul.replaceChildren(...q.map((file) => {
    const li = document.createElement('li');
    const name = document.createElement('span'); name.textContent = file.name;
    const rm = document.createElement('button'); rm.type = 'button'; rm.textContent = 'Remove';
    rm.setAttribute('aria-label', 'Remove ' + file.name + ' from the queue');
    rm.addEventListener('click', () => {
      if (!batch) return;
      const i = batch.queue.indexOf(file);
      if (i < 0) return;   // already started
      batch.queue.splice(i, 1); batch.total--;
      renderQueue();
    });
    li.append(name, rm);
    return li;
  }));
}

$('uploadbtn').addEventListener('click', () => {
  const dbName = db();
  const files = Array.from($('ifile').files);
  if (!dbName) return show('importerror', 'Select or create a database first.');
  if (!files.length) return show('importerror', 'Choose a file to upload.');
  if (batch && batch.cancelled) return show('importerror', 'The batch is being cancelled; wait for it to stop.');
  // Simplest safe rule: files added mid-batch go to the running batch's database only.
  if (batch && dbName !== batch.db) return show('importerror', 'Uploads are running into "' + batch.db + '"; select that database to add more files.');
  show('importerror', '');
  $('ifile').value = '';
  if (batch) { batch.queue.push(...files); batch.total += files.length; renderQueue(); return; }
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
    const active = jobs.find((j) => !TERMINAL.includes(j.status));
    lastJobs = jobs;
    renderBanner();
    if (active) { renderJob(active); track(active.id); }
  } catch (e) { /* the panel just starts idle */ }
})();

// ---- "contains" search: exact match answered inline; substring match is a background job ----

const SEARCH_MIN_QUERY_LENGTH = 4;   // mirrors SearchRunner.MinQueryLength; the server enforces this regardless
let searchEvents = null;
let currentSearchJob = null;
let currentSearchId = null;   // known as soon as the start request returns, even before the first SSE frame

function closeSearchEvents() { if (searchEvents) { searchEvents.close(); searchEvents = null; } }

function renderSearchJob(j) {
  currentSearchJob = j;
  currentSearchId = j.id;
  $('searchjob').hidden = false;
  $('searchtitle').textContent = 'Searching ' + j.db + ' for "' + j.source + '"';
  const st = $('searchstatus'); st.textContent = j.status; st.className = 'badge ' + j.status;
  const bar = $('searchbar');
  const done = TERMINAL.includes(j.status);
  if (done) { bar.max = 100; bar.value = 100; } else bar.removeAttribute('value');   // indeterminate: no percentage, just a counter below
  $('searchstats').replaceChildren(
    ...[['Scanned', fmt(j.scannedCount) + ' entries'], ['Found', fmt(j.matchedCount) + (j.capped ? ' (stopped at the cap)' : '')], ['Elapsed', fmtElapsed(j)]]
      .flatMap(([k, v]) => { const dt = document.createElement('dt'); dt.textContent = k; const dd = document.createElement('dd'); dd.textContent = v; return [dt, dd]; }));
  show('searchjoberror', j.error || '');
  const list = $('searchresults');
  list.replaceChildren(...(j.matches || []).map((w) => { const li = document.createElement('li'); li.textContent = w; return li; }));   // textContent only: never innerHTML
  if (j.capped) {
    const li = document.createElement('li');
    li.textContent = 'Showing the first ' + j.matches.length + ' of at least ' + fmt(j.matchedCount) + ' matches.';
    list.append(li);
  }
  $('searchcancel').hidden = done;
  if (done) closeSearchEvents();
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
    } catch (e) { fail('searchjoberror', e); closeSearchEvents(); }
  };
}

$('searchbtn').addEventListener('click', async () => {
  const dbName = db();
  const query = $('squery').value;
  show('searcherror', '');
  if (!dbName) { show('searcherror', 'Select a database first.'); return; }
  if (query.length < SEARCH_MIN_QUERY_LENGTH) { show('searcherror', 'Enter at least ' + SEARCH_MIN_QUERY_LENGTH + ' characters.'); return; }
  try {
    const res = await post('/api/dbs/' + enc(dbName) + '/search', { query });
    currentSearchId = res.id;
    $('searchjob').hidden = false;
    show('searchexact', 'Exact match: ' + (res.exactFound ? 'found.' : 'not found.') + ' Scanning the rest of the database for "' + res.query + '" in the background...');
    trackSearch(res.id);
  } catch (e) { fail('searcherror', e); }
});

$('searchcancel').addEventListener('click', async () => {
  if (!currentSearchId) return;
  try { renderSearchJob(await api('/api/jobs/' + enc(currentSearchId) + '/cancel', { method: 'POST' })); }
  catch (e) { fail('searchjoberror', e); }
});

$('logout').addEventListener('click', async () => {
  await fetch('/logout', { method: 'POST', credentials: 'same-origin' });
  location.href = '/login.html';
});

loadDbs();
