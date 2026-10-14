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
