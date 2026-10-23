(() => {
  'use strict';
  if (window.__shokoMpcLoaded) return;
  window.__shokoMpcLoaded = true;
  const script = document.currentScript;
  const base = new URL(script.src).pathname.replace(/\/api\/plugin\/ShokoMpc\/webui\.js$/, '');
  const api = base + '/api/plugin/ShokoMpc';
  const server = location.origin + base;
  const buttons = new WeakMap();
  let scheduled = false;
  let frame = 0;
  function key() {
    try { return JSON.parse(localStorage.getItem('apiSession') || '{}').apikey || ''; }
    catch { return ''; }
  }
  async function request(path, options = {}) {
    const apikey = key();
    if (!apikey) throw new Error('Sign in to Shoko first.');
    const response = await fetch(path, { ...options, credentials: 'same-origin', redirect: 'error',
      headers: { apikey, ...options.headers } });
    if (!response.ok) {
      let body = {}; try { body = await response.json(); } catch {}
      throw new Error(body.error || body.detail || `Shoko returned HTTP ${response.status}.`);
    }
    return response.json();
  }
  function show(message, href) {
    let notice = document.getElementById('shoko-mpc-notice');
    if (!notice) {
      notice = document.createElement('div'); notice.id = 'shoko-mpc-notice';
      notice.setAttribute('role', 'status');
      Object.assign(notice.style, { position: 'fixed', bottom: '20px', right: '20px', zIndex: '2147483646',
        maxWidth: '410px', padding: '16px', borderRadius: '10px', color: '#edf3fc', background: '#172236',
        border: '1px solid #526481', boxShadow: '0 5px 25px #0005', font: '14px/1.5 system-ui' });
      document.body.append(notice);
    }
    notice.replaceChildren(document.createTextNode(message + ' '));
    if (href) {
      const retry = document.createElement('a'); retry.href = href; retry.textContent = 'Open MPC-HC';
      retry.style.color = '#8dd3ff'; notice.append(retry, document.createTextNode(' · '));
    }
    const setup = document.createElement('a'); setup.href = api + '/dashboard'; setup.target = '_blank';
    setup.rel = 'noopener noreferrer'; setup.textContent = 'Windows setup'; setup.style.color = '#8dd3ff';
    const close = document.createElement('button'); close.type = 'button'; close.textContent = 'Dismiss';
    close.style.marginLeft = '12px'; close.onclick = () => notice.remove();
    notice.append(setup, close);
  }
  async function play(episodeId, button) {
    button.disabled = true; button.textContent = 'Opening…';
    try {
      // Lower-case e is the AniDB episode ID, obtained from the episode's actual AniDB link.
      const playlist = await request(base + '/api/v3/Playlist/Generate?playlist=e' + episodeId);
      const files = [...new Set(playlist.flatMap(item => (item.Parts || []).map(part => part.ID)))];
      if (!files.length) throw new Error('No playable file was found for this episode.');
      const result = await request(api + '/launch', { method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ fileIds: files }) });
      const href = 'shoko-mpc://play/?server=' + encodeURIComponent(server) + '&ticket=' + encodeURIComponent(result.ticket);
      show('Playback link sent. If MPC-HC does not open, use the link below.', href);
      // A visible retry link covers browsers that require a fresh gesture after async requests.
      const link = document.createElement('a'); link.href = href; link.hidden = true;
      document.body.append(link); link.click(); link.remove();
    } catch (error) { show(error.message || 'Could not open MPC-HC.'); }
    finally { button.disabled = false; button.textContent = '▶ MPC-HC'; }
  }
  function scan() {
    scheduled = false;
    if (!/\/collection\/series\/\d+(?:\/|$)/.test(location.pathname)) return;
    for (const anchor of document.querySelectorAll('a[href]')) {
      let url; try { url = new URL(anchor.href); } catch { continue; }
      if (!['anidb.net', 'www.anidb.net'].includes(url.hostname)) continue;
      const match = /^\/episode\/(\d+)\/?$/.exec(url.pathname);
      if (!match) continue;
      const id = Number(match[1]);
      if (!Number.isSafeInteger(id) || id <= 0) continue;
      const previous = buttons.get(anchor);
      if (previous && previous.id === id && previous.button.isConnected) continue;
      if (previous) previous.button.remove();
      const button = document.createElement('button'); button.type = 'button'; button.textContent = '▶ MPC-HC';
      button.className = 'shoko-mpc-play'; button.title = 'Play this episode in MPC-HC';
      button.setAttribute('aria-label', 'Play episode ' + id + ' in MPC-HC');
      Object.assign(button.style, { padding: '3px 9px', border: '1px solid currentColor', borderRadius: '5px',
        color: 'inherit', background: 'transparent', cursor: 'pointer', font: 'inherit', whiteSpace: 'nowrap' });
      button.onclick = event => { event.preventDefault(); event.stopPropagation(); void play(id, button); };
      anchor.insertAdjacentElement('afterend', button); buttons.set(anchor, { id, button });
    }
  }
  function schedule() { if (!scheduled) { scheduled = true; frame = requestAnimationFrame(scan); } }
  const observer = new MutationObserver(schedule);
  observer.observe(document.documentElement, { childList: true, subtree: true, attributes: true, attributeFilter: ['href'] });
  addEventListener('pagehide', () => { observer.disconnect(); cancelAnimationFrame(frame); scheduled = false; });
  addEventListener('pageshow', () => { observer.observe(document.documentElement, { childList: true, subtree: true, attributes: true, attributeFilter: ['href'] }); schedule(); });
  addEventListener('popstate', schedule);
  schedule();
})();
