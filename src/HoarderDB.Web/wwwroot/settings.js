const $ = (id) => document.getElementById(id);

$('logout').addEventListener('click', async () => {
  await fetch('/logout', { method: 'POST', credentials: 'same-origin' });
  location.href = '/login.html';
});

$('pwform').addEventListener('submit', async (e) => {
  e.preventDefault();
  const err = $('error'), ok = $('ok');
  err.textContent = ''; ok.textContent = '';
  const current = $('current').value, next = $('newpw').value;
  if (next.length < 12 || next.length > 128) { err.textContent = 'New password must be 12-128 characters.'; return; }
  if (next !== $('confirm').value) { err.textContent = 'The new passwords do not match.'; return; }
  $('save').disabled = true;
  try {
    const r = await fetch('/api/account/password', {
      method: 'POST', credentials: 'same-origin',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ currentPassword: current, newPassword: next }),
    });
    if (r.status === 204) {
      $('pwform').reset();
      ok.textContent = 'Password changed. Other sessions have been signed out.';
    } else if (r.status === 401) {
      location.href = '/login.html';   // session ended
    } else if (r.status === 403) {
      err.textContent = 'Current password is incorrect.';
    } else if (r.status === 429) {
      err.textContent = 'Too many attempts. Try again later.';
    } else if (r.status === 400) {
      let t = '';
      try { t = (await r.json()).title || ''; } catch { /* not JSON */ }
      err.textContent = t || 'Could not change password.';
    } else {
      err.textContent = 'Could not change password.';
    }
  } catch { err.textContent = 'Could not reach the server.'; }
  finally { $('save').disabled = false; }
});

// ---- Colour mode ----
$('theme').value = window.HoarderTheme ? window.HoarderTheme.get() : 'auto';
$('theme').addEventListener('change', () => { if (window.HoarderTheme) window.HoarderTheme.set($('theme').value); });

// ---- Compaction schedule and status ----
const pad = (n) => String(n).padStart(2, '0');

function fmtBytes(n) {
  const gib = n / 1073741824;
  return gib >= 1024 ? (gib / 1024).toFixed(1) + ' TiB' : gib.toFixed(1) + ' GiB';
}
function fmtDuration(s) {
  if (s < 60) return s + ' s';
  const m = Math.floor(s / 60);
  return m < 60 ? m + ' min ' + (s % 60) + ' s' : Math.floor(m / 60) + ' h ' + (m % 60) + ' min';
}
function fmtUtc(iso) { return new Date(iso).toISOString().slice(0, 16).replace('T', ' ') + ' UTC'; }

function showSchedule(c) {
  $('sched-enabled').checked = c.enabled;
  $('sched-day').value = String(c.day);
  $('sched-time').value = pad(c.hour) + ':' + pad(c.minute);
  $('sched-zone').textContent = c.timeZone + ', UTC' + c.utcOffset;
  $('sched-next').textContent = c.nextRunUtc
    ? 'Next run: ' + c.nextRunLocal + ' ' + c.timeZone + ' (' + fmtUtc(c.nextRunUtc) + ')'
    : 'Compaction is switched off.';
}

function showStatus(st) {
  const ul = $('status-list');
  ul.textContent = '';
  const add = (t) => { const li = document.createElement('li'); li.textContent = t; ul.appendChild(li); return li; };
  const d = st.disk;
  if (d.data) add('Data disk: ' + fmtBytes(d.data.freeBytes) + ' free of ' + fmtBytes(d.data.totalBytes));
  if (d.spool) add('Search temp disk: ' + fmtBytes(d.spool.freeBytes) + ' free of ' + fmtBytes(d.spool.totalBytes));
  if (st.compactionRunning) add('A compaction is running or queued now.');
  const lc = st.lastCompaction;
  if (!lc) { add('Last compaction: none yet.'); return; }
  add('Last compaction (' + lc.trigger + '): ' + lc.result + ', ' + new Date(lc.startedAt).toLocaleString()
    + ' (' + fmtUtc(lc.startedAt) + '), took ' + fmtDuration(lc.durationSeconds));
  const sub = document.createElement('ul');
  sub.className = 'plain';
  for (const db of lc.databases) {
    const li = document.createElement('li');
    li.textContent = db.name + ': ' + db.result + ', ' + fmtDuration(db.durationSeconds);
    sub.appendChild(li);
  }
  ul.lastChild.appendChild(sub);
}

async function loadSettings() {
  try {
    const r = await fetch('/api/settings', { credentials: 'same-origin' });
    if (r.status === 401) { location.href = '/login.html'; return; }
    if (!r.ok) { $('status-error').textContent = 'Could not load the settings.'; return; }
    const j = await r.json();
    showSchedule(j.compaction);
    showStatus(j.status);
  } catch { $('status-error').textContent = 'Could not reach the server.'; }
}

$('schedform').addEventListener('submit', async (e) => {
  e.preventDefault();
  const err = $('sched-error'), ok = $('sched-ok');
  err.textContent = ''; ok.textContent = '';
  const m = /^(\d{1,2}):(\d{2})/.exec($('sched-time').value);
  if (!m) { err.textContent = 'Enter a time.'; return; }
  $('sched-save').disabled = true;
  try {
    const r = await fetch('/api/settings/compaction', {
      method: 'PUT', credentials: 'same-origin',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        enabled: $('sched-enabled').checked, day: Number($('sched-day').value),
        hour: Number(m[1]), minute: Number(m[2]),
      }),
    });
    if (r.status === 200) {
      showSchedule(await r.json());
      ok.textContent = 'Schedule saved.';
    } else if (r.status === 401) {
      location.href = '/login.html';
    } else if (r.status === 429) {
      err.textContent = 'Too many attempts. Try again later.';
    } else if (r.status === 400) {
      err.textContent = 'That schedule is not valid.';
    } else {
      err.textContent = 'Could not save the schedule.';
    }
  } catch { err.textContent = 'Could not reach the server.'; }
  finally { $('sched-save').disabled = false; }
});

loadSettings();
