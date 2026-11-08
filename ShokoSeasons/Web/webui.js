(() => {
  'use strict';
  const script = document.getElementById('shoko-seasons-script');
  if (!script) return;
  const home = new URL('./', script.src).href;
  function mount() {
    if (document.getElementById('shoko-seasons-link')) return;
    const anchor = document.createElement('a'); anchor.id = 'shoko-seasons-link'; anchor.href = home;
    anchor.textContent = '◈ Seasonal anime'; anchor.title = 'Browse seasonal anime and follow Sonarr downloads';
    anchor.style.cssText = 'position:fixed;right:18px;bottom:18px;z-index:1000;background:#284c86;color:#edf3ff;padding:11px 16px;border:1px solid #5b80b9;border-radius:8px;font:600 13px system-ui;text-decoration:none;box-shadow:0 3px 12px #0004';
    anchor.onfocus = () => { anchor.style.outline = '3px solid #a7c9ff'; };
    anchor.onblur = () => { anchor.style.outline = ''; };
    document.body.append(anchor);
  }
  mount();
})();
