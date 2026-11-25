(() => {
  'use strict';
  if (window.__shokoMpcLoaded) return;
  window.__shokoMpcLoaded = true;
  const script = document.currentScript;
  const base = new URL(script.src).pathname.replace(/\/api\/plugin\/ShokoMpc\/webui\.js$/, '');
  const api = base + '/api/plugin/ShokoMpc';
  const server = location.origin + base;
  const buttons = new Map();
  const castTargetKey = 'shoko-cast-target';
  const castTargetId = /^(miracast|(dlna|cast):uuid:[0-9A-Za-z-]{8,64})$/;
  let scheduled = false;
  let frame = 0;
  let castNotice;
  let castTimer;
  let castRevision = 0;
  let castPolling = false;
  let castPending = false;
  let castActive = false;
  let castReplacing = false;
  let rememberedTarget = readCastTarget();
  let activeCastTarget;
  let castMenu;
  let lastCast;
  function readCastTarget() {
    try {
      const target = JSON.parse(localStorage.getItem(castTargetKey));
      if (target && typeof target === 'object' && !Array.isArray(target) &&
          typeof target.id === 'string' && castTargetId.test(target.id) &&
          typeof target.name === 'string' && target.name.length > 0) {
        return { id: target.id, name: target.name };
      }
    } catch {}
    return null;
  }
  function key() {
    try {
      const state = JSON.parse(sessionStorage.getItem('state') || '{}');
      // Current tab state takes precedence, including an explicitly signed-out session.
      if (state.apiSession) return state.apiSession.apikey || '';
      return JSON.parse(localStorage.getItem('apiSession') || '{}').apikey || '';
    }
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
  function pauseCastPolling() {
    clearTimeout(castTimer);
    return ++castRevision;
  }
  function showCastNotice(message, { canStop = castActive, canRetry = false } = {}) {
    if (!castNotice) {
      const element = document.createElement('div'); element.id = 'shoko-cast-notice';
      element.setAttribute('role', 'status');
      Object.assign(element.style, { position: 'fixed', bottom: '20px', right: '20px', zIndex: '2147483647',
        maxWidth: '410px', padding: '16px', borderRadius: '10px', color: '#edf3fc', background: '#172236',
        border: '1px solid #526481', boxShadow: '0 5px 25px #0005', font: '14px/1.5 system-ui' });
      const text = document.createTextNode('');
      const stop = document.createElement('button'); stop.type = 'button'; stop.textContent = 'Stop TV';
      stop.onclick = () => void stopCast();
      const retry = document.createElement('button'); retry.type = 'button'; retry.textContent = 'Retry';
      retry.onclick = () => { if (lastCast) void playOnTv(lastCast.episodeId, lastCast.button); };
      const dismiss = document.createElement('button'); dismiss.type = 'button'; dismiss.textContent = 'Dismiss';
      dismiss.onclick = () => {
        castPolling = false; pauseCastPolling();
        element.remove(); castNotice = null;
      };
      for (const button of [stop, retry, dismiss]) button.style.marginLeft = '12px';
      element.append(text, stop, retry, dismiss); document.body.append(element);
      castNotice = { text, stop, retry };
    }
    castNotice.text.nodeValue = message;
    castNotice.stop.hidden = !canStop;
    castNotice.retry.hidden = !canRetry || !lastCast;
  }
  function showCastStatus(status) {
    castActive = ['connecting', 'playing', 'stopping'].includes(status.state);
    if (status.state !== 'connecting') castReplacing = false;
    const tvName = activeCastTarget && (!status.target || status.target === activeCastTarget.id)
      ? activeCastTarget.name : 'the TV';
    switch (status.state) {
      case 'connecting':
        showCastNotice(castReplacing ? 'Switching ' + tvName + ' to this episode…' : 'Connecting to ' + tvName + '…');
        break;
      case 'playing':
        showCastNotice((status.title ? 'Playing "' + status.title + '"' : 'Playing') + ' on ' + tvName);
        break;
      case 'stopping':
        showCastNotice('Stopping the TV…', { canStop: false });
        break;
      case 'idle':
        showCastNotice('Stopped.');
        break;
      case 'error':
        showCastNotice(status.error || 'The TV could not play this episode.', { canRetry: true });
        break;
    }
  }
  function scheduleCastPoll() {
    clearTimeout(castTimer);
    if (castNotice && castPolling && !castPending) castTimer = setTimeout(pollCast, 2000);
  }
  async function pollCast() {
    const revision = castRevision;
    try {
      const status = await request(api + '/cast');
      if (revision === castRevision) showCastStatus(status);
    } catch (error) {
      if (revision === castRevision) showCastNotice(error.message || 'Could not check the TV.');
    } finally {
      if (revision === castRevision) scheduleCastPoll();
    }
  }
  async function playOnTv(episodeId, button) {
    if (castPending) return;
    castPending = true;
    const target = rememberedTarget;
    const revision = pauseCastPolling();
    lastCast = { episodeId, button };
    button.disabled = true; button.textContent = 'Starting…';
    try {
      const playlist = await request(base + '/api/v3/Playlist/Generate?playlist=e' + episodeId);
      const files = [...new Set(playlist.flatMap(item => (item.Parts || []).map(part => part.ID)))];
      if (!files.length) throw new Error('No playable file was found for this episode.');
      if (files.length > 1) throw new Error("Multi-part episodes can't play on the TV yet.");
      const body = { fileIds: files };
      if (target) body.targetId = target.id;
      await request(api + '/cast', { method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(body) });
      activeCastTarget = target;
      castReplacing = castActive;
      if (revision === castRevision) {
        castPolling = true;
        showCastStatus({ state: 'connecting' });
      } else {
        castActive = true;
      }
    } catch (error) {
      if (revision === castRevision) showCastNotice(error.message || 'Could not start TV playback.', { canRetry: true });
    } finally {
      castPending = false;
      button.disabled = false;
      labelTvButton(episodeId, button);
      if (revision === castRevision) scheduleCastPoll();
    }
  }
  async function stopCast() {
    if (castPending || !castActive) return;
    castPending = true;
    const revision = pauseCastPolling();
    try {
      const status = await request(api + '/cast', { method: 'DELETE' });
      if (revision === castRevision) showCastStatus(status);
    } catch (error) {
      if (revision === castRevision) showCastNotice(error.message || 'Could not stop the TV.');
    } finally {
      castPending = false;
      if (revision === castRevision) scheduleCastPoll();
    }
  }
  function labelTvButton(id, button) {
    const name = rememberedTarget?.name;
    if (!button.disabled) button.textContent = '▶ ' + (name ?? 'TV');
    button.title = 'Play this episode on ' + (name ?? 'the TV');
    button.setAttribute('aria-label', 'Play episode ' + id + ' on ' + (name ?? 'the TV'));
  }
  function closeCastMenu(restoreFocus = false) {
    if (!castMenu) return;
    const { element, opener } = castMenu;
    castMenu = undefined;
    element.remove();
    opener.setAttribute('aria-expanded', 'false');
    if (restoreFocus) opener.focus();
  }
  function positionCastMenu() {
    if (!castMenu) return;
    const { element, opener } = castMenu;
    const anchor = opener.getBoundingClientRect();
    const { offsetWidth: width, offsetHeight: height } = element;
    const below = anchor.bottom + 4;
    const top = below + height <= innerHeight - 8 ? below : anchor.top - height - 4;
    element.style.top = Math.max(8, top) + 'px';
    element.style.left = Math.max(8, Math.min(anchor.right, innerWidth - 8) - width) + 'px';
  }
  function showTargetMenuMessage(menu, message) {
    const status = document.createElement('div');
    status.setAttribute('role', 'status');
    status.textContent = message;
    menu.replaceChildren(status);
  }
  async function toggleCastMenu(opener) {
    const wasOpen = castMenu?.opener === opener;
    closeCastMenu();
    if (wasOpen) return;

    const menu = document.createElement('div');
    menu.id = 'shoko-cast-targets';
    menu.tabIndex = -1;
    menu.setAttribute('role', 'menu');
    menu.setAttribute('aria-label', 'Choose a TV');
    Object.assign(menu.style, { position: 'fixed', zIndex: '2147483647', boxSizing: 'border-box',
      minWidth: '150px', maxWidth: 'min(280px, calc(100vw - 16px))', maxHeight: 'calc(100vh - 16px)',
      overflow: 'auto', overflowWrap: 'anywhere', padding: '6px', borderRadius: '5px', color: '#edf3fc',
      background: '#172236', border: '1px solid #526481', boxShadow: '0 5px 25px #0005', font: '14px/1.5 system-ui' });
    showTargetMenuMessage(menu, 'Looking for TVs…');
    document.body.append(menu);
    castMenu = { element: menu, opener };
    opener.setAttribute('aria-expanded', 'true');
    positionCastMenu();
    menu.focus({ preventScroll: true });

    try {
      const targets = await request(api + '/cast/targets');
      if (castMenu?.element !== menu) return;
      if (!targets.length) {
        showTargetMenuMessage(menu, 'No TVs found.');
      } else {
        const selectedId = rememberedTarget?.id ?? 'miracast';
        menu.replaceChildren();
        for (const { id, name, available } of targets) {
          const item = document.createElement('button');
          item.type = 'button';
          item.tabIndex = -1;
          item.setAttribute('role', 'menuitemradio');
          item.setAttribute('aria-checked', String(id === selectedId));
          item.textContent = name + (available ? '' : ' (asleep)');
          item.style.cssText = opener.style.cssText;
          Object.assign(item.style, { display: 'block', width: '100%', margin: '0 0 4px', textAlign: 'left',
            whiteSpace: 'normal', fontWeight: id === selectedId ? 'bold' : 'normal' });
          item.onclick = () => {
            rememberedTarget = { id, name };
            try { localStorage.setItem(castTargetKey, JSON.stringify(rememberedTarget)); } catch {}
            for (const row of buttons.values()) labelTvButton(row.id, row.tv);
            closeCastMenu(true);
          };
          menu.append(item);
        }
        (menu.querySelector('[aria-checked="true"]') ?? menu.firstElementChild).focus({ preventScroll: true });
      }
    } catch (error) {
      if (castMenu?.element !== menu) return;
      showTargetMenuMessage(menu, error.message || 'Could not list TVs.');
    }
    positionCastMenu();
  }
  function tvButtons(id, mpcButton) {
    const button = document.createElement('button'); button.type = 'button';
    button.className = 'shoko-cast-play';
    labelTvButton(id, button);
    button.style.cssText = mpcButton.style.cssText;
    button.style.marginLeft = '4px';
    button.onclick = event => { event.preventDefault(); event.stopPropagation(); void playOnTv(id, button); };
    mpcButton.insertAdjacentElement('afterend', button);

    const menuButton = document.createElement('button'); menuButton.type = 'button';
    menuButton.className = 'shoko-cast-menu'; menuButton.textContent = '▾';
    menuButton.setAttribute('aria-label', 'Choose a TV');
    menuButton.setAttribute('aria-haspopup', 'menu');
    menuButton.setAttribute('aria-expanded', 'false');
    menuButton.style.cssText = mpcButton.style.cssText;
    menuButton.style.marginLeft = '2px';
    menuButton.onclick = event => { event.preventDefault(); event.stopPropagation(); void toggleCastMenu(menuButton); };
    button.insertAdjacentElement('afterend', menuButton);
    return { tv: button, menuButton };
  }
  function removeButtons(row) {
    if (castMenu?.opener === row.menuButton) closeCastMenu();
    row.button.remove();
    row.tv.remove();
    row.menuButton.remove();
  }
  function scan() {
    scheduled = false;
    if (!/\/collection\/series\/\d+(?:\/|$)/.test(location.pathname)) {
      closeCastMenu();
      return;
    }
    const episodeLinks = new Set();
    for (const anchor of document.querySelectorAll('a[href]')) {
      let url; try { url = new URL(anchor.href); } catch { continue; }
      if (!['anidb.net', 'www.anidb.net'].includes(url.hostname)) continue;
      const match = /^\/episode\/(\d+)\/?$/.exec(url.pathname);
      if (!match) continue;
      const id = Number(match[1]);
      if (!Number.isSafeInteger(id) || id <= 0) continue;
      episodeLinks.add(anchor);
      const previous = buttons.get(anchor);
      if (previous && previous.id === id && anchor.nextElementSibling === previous.button) {
        if (previous.button.nextElementSibling !== previous.tv) {
          previous.button.insertAdjacentElement('afterend', previous.tv);
        }
        if (previous.tv.nextElementSibling !== previous.menuButton) {
          previous.tv.insertAdjacentElement('afterend', previous.menuButton);
        }
        continue;
      }
      if (previous) removeButtons(previous);
      const button = document.createElement('button'); button.type = 'button'; button.textContent = '▶ MPC-HC';
      button.className = 'shoko-mpc-play'; button.title = 'Play this episode in MPC-HC';
      button.setAttribute('aria-label', 'Play episode ' + id + ' in MPC-HC');
      Object.assign(button.style, { padding: '3px 9px', border: '1px solid currentColor', borderRadius: '5px',
        color: 'inherit', background: 'transparent', cursor: 'pointer', font: 'inherit', whiteSpace: 'nowrap' });
      button.onclick = event => { event.preventDefault(); event.stopPropagation(); void play(id, button); };
      anchor.insertAdjacentElement('afterend', button);
      buttons.set(anchor, { id, button, ...tvButtons(id, button) });
    }
    for (const [anchor, previous] of buttons) {
      if (episodeLinks.has(anchor)) continue;
      removeButtons(previous);
      buttons.delete(anchor);
    }
  }
  function schedule() { if (!scheduled) { scheduled = true; frame = requestAnimationFrame(scan); } }
  const observer = new MutationObserver(schedule);
  observer.observe(document.documentElement, { childList: true, subtree: true, attributes: true, attributeFilter: ['href'] });
  addEventListener('pagehide', () => { observer.disconnect(); cancelAnimationFrame(frame); scheduled = false; });
  addEventListener('pageshow', () => { observer.observe(document.documentElement, { childList: true, subtree: true, attributes: true, attributeFilter: ['href'] }); schedule(); });
  addEventListener('pagehide', pauseCastPolling);
  addEventListener('pagehide', () => closeCastMenu());
  addEventListener('pageshow', scheduleCastPoll);
  addEventListener('popstate', schedule);
  addEventListener('resize', positionCastMenu);
  document.addEventListener('scroll', positionCastMenu, true);
  document.addEventListener('click', event => {
    if (castMenu && !castMenu.element.contains(event.target) && !castMenu.opener.contains(event.target)) closeCastMenu();
  }, true);
  document.addEventListener('keydown', event => {
    if (!castMenu) return;
    if (event.key === 'Escape' || event.key === 'Tab') {
      if (event.key === 'Escape') event.preventDefault();
      closeCastMenu(true);
      return;
    }
    const { element } = castMenu;
    if (!element.contains(event.target) || !['ArrowDown', 'ArrowUp', 'Home', 'End'].includes(event.key)) return;
    const items = [...element.querySelectorAll('[role="menuitemradio"]')];
    if (!items.length) return;
    event.preventDefault();
    const current = items.indexOf(document.activeElement);
    let next = 0;
    switch (event.key) {
      case 'End': next = items.length - 1; break;
      case 'ArrowDown': next = (current + 1) % items.length; break;
      case 'ArrowUp': next = current > 0 ? current - 1 : items.length - 1; break;
    }
    items[next].focus();
  }, true);
  schedule();
})();
