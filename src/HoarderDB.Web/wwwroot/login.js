document.getElementById('login').addEventListener('submit', async (e) => {
  e.preventDefault();
  const err = document.getElementById('error');
  err.textContent = '';
  const body = {
    username: document.getElementById('username').value,
    password: document.getElementById('password').value,
  };
  try {
    const r = await fetch('/login', {
      method: 'POST', credentials: 'same-origin',
      headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body),
    });
    if (r.ok) { location.href = '/'; return; }
    err.textContent = r.status === 429 ? 'Too many attempts. Wait a minute and retry.' : 'Invalid username or password.';
  } catch { err.textContent = 'Could not reach the server.'; }
});
