(() => {
  'use strict';
  const script = document.getElementById('shoko-seasons-script');
  if (!script) return;
  const home = new URL('./', script.src).href;
  const dashboardPath = new URL(home).pathname.replace(/\/api\/plugin\/ShokoSeasons\/$/, '/webui/dashboard');
  const preference = 'shoko-seasons-dashboard-hidden';
  let hidden = false;
  try { hidden = localStorage.getItem(preference) === 'true'; } catch { /* Browser storage may be disabled. */ }

  function mountDashboard() {
    const existing = document.getElementById('shoko-seasons-dashboard');
    const grid = location.pathname.replace(/\/$/, '') === dashboardPath && document.querySelector('.react-grid-layout');
    if (!grid) { existing?.remove(); return; }
    if (existing) return;
    // Keep our card outside React Grid Layout: Shoko has no plugin widget registry.
    // This preserves the user's built-in card positions and resize settings.
    const card = document.createElement('section'); card.id = 'shoko-seasons-dashboard';
    card.setAttribute('aria-label', 'Seasonal anime dashboard card');
    card.className = 'rounded-lg border border-panel-border bg-panel-background';
    card.style.cssText = 'margin-bottom:24px;overflow:hidden;min-width:0';
    const header = document.createElement('div');
    header.style.cssText = 'display:flex;align-items:center;flex-wrap:wrap;gap:16px;padding:16px 24px';
    const title = document.createElement('h2'); title.textContent = 'Seasonal anime';
    title.className = 'text-xl font-semibold'; title.style.marginRight = 'auto';
    const full = document.createElement('a'); full.href = home; full.textContent = 'Open full catalog ↗';
    const toggle = document.createElement('button'); toggle.type = 'button';
    toggle.setAttribute('aria-controls', 'shoko-seasons-dashboard-frame');
    toggle.style.cssText = 'border:1px solid currentColor;border-radius:6px;padding:6px 12px;cursor:pointer';
    function render() {
      toggle.textContent = hidden ? 'Add seasonal anime card' : 'Hide card';
      toggle.setAttribute('aria-expanded', String(!hidden));
      card.querySelector('iframe')?.remove();
      if (hidden) return;
      const frame = document.createElement('iframe'); frame.id = 'shoko-seasons-dashboard-frame';
      frame.title = 'Seasonal anime catalog'; frame.src = home + '?dashboard=1';
      frame.style.cssText = 'display:block;width:100%;height:1280px;border:0';
      card.append(frame);
    }
    toggle.onclick = () => {
      hidden = !hidden;
      try { localStorage.setItem(preference, String(hidden)); } catch { /* Keep the control working without persistence. */ }
      render();
    };
    header.append(title, full, toggle); card.append(header); render();
    grid.before(card);
  }
  function mount() {
    mountDashboard();
    if (document.getElementById('shoko-seasons-link')) return;
    const anchor = document.createElement('a'); anchor.id = 'shoko-seasons-link'; anchor.href = home;
    anchor.textContent = '◈ Seasonal anime'; anchor.title = 'Browse seasonal anime and follow Sonarr downloads';
    anchor.style.cssText = 'position:fixed;right:18px;bottom:18px;z-index:1000;background:var(--button-primary,#284c86);color:var(--button-primary-text,#edf3ff);padding:11px 16px;border:1px solid var(--button-primary-border,#5b80b9);border-radius:8px;font:inherit;text-decoration:none;box-shadow:0 3px 12px #0004';
    anchor.onfocus = () => { anchor.style.outline = '3px solid var(--panel-text-primary,#a7c9ff)'; };
    anchor.onblur = () => { anchor.style.outline = ''; };
    document.body.append(anchor);
  }
  mount();
  // Shoko mounts and replaces its dashboard asynchronously during SPA navigation.
  let scheduled = false, animationFrame;
  const observer = new MutationObserver(() => {
    if (scheduled) return;
    scheduled = true;
    animationFrame = requestAnimationFrame(() => { scheduled = false; mount(); });
  });
  observer.observe(document.body, { childList: true, subtree: true });
  window.addEventListener('pagehide', () => { observer.disconnect(); cancelAnimationFrame(animationFrame); scheduled = false; });
  window.addEventListener('pageshow', () => { observer.observe(document.body, { childList: true, subtree: true }); mount(); });
  window.addEventListener('popstate', mount);
})();
