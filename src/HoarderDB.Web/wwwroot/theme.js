// Colour mode (auto/light/dark). Loaded synchronously in <head> on every page (no defer) so the
// attribute is set before first paint. The stored value is parsed against an allow-list, never used as markup.
(function () {
  var KEY = 'hoarderdb.theme';
  function get() {
    try {
      var v = localStorage.getItem(KEY);
      return v === 'light' || v === 'dark' ? v : 'auto';
    } catch (e) { return 'auto'; }   // storage blocked: follow the system
  }
  function apply(mode) {
    var el = document.documentElement;
    if (mode === 'light' || mode === 'dark') el.setAttribute('data-theme', mode);
    else el.removeAttribute('data-theme');
  }
  function set(mode) {
    if (mode !== 'light' && mode !== 'dark') mode = 'auto';
    try {
      if (mode === 'auto') localStorage.removeItem(KEY); else localStorage.setItem(KEY, mode);
    } catch (e) { /* blocked: applies for this page only */ }
    apply(mode);
  }
  apply(get());
  window.HoarderTheme = { get: get, set: set };
})();
