(() => {
  'use strict';
  const base = location.pathname.replace(/\/$/, '');
  const root = base.slice(0, base.lastIndexOf('/api/plugin/'));
  const $ = id => document.getElementById(id);
  const dashboard = new URLSearchParams(location.search).get('dashboard') === '1';
  if (dashboard) {
    document.body.classList.add('dashboard');
    document.querySelectorAll('a').forEach(a => { if (a.origin === location.origin) a.target = '_top'; });
  }
  function syncDashboardTheme() {
    if (!dashboard) return;
    try {
      // Theme variables are scoped to Shoko's body, not its document root.
      // Read only presentation values from our same-origin host card.
      const host = window.frameElement?.parentElement;
      if (!host) return;
      const theme = parent.getComputedStyle(host);
      const properties = ['--panel-background', '--panel-background-alt', '--panel-background-overlay',
        '--panel-border', '--panel-input', '--panel-tags', '--panel-text', '--panel-text-primary',
        '--panel-text-important', '--panel-text-warning', '--panel-menu-item-background',
        '--panel-menu-item-background-hover', '--panel-menu-item-text', '--button-primary',
        '--button-primary-border', '--button-primary-hover', '--button-primary-text',
        '--button-secondary', '--button-secondary-hover', '--button-secondary-text'];
      for (const name of properties) {
        const value = theme.getPropertyValue(name).trim();
        if (value) document.body.style.setProperty(name, value);
        else document.body.style.removeProperty(name);
      }
      document.body.style.fontFamily = theme.fontFamily;
      document.body.style.fontWeight = theme.fontWeight;
      document.body.style.colorScheme = theme.colorScheme;
      // Reuse any already loaded custom fonts without loading another theme bundle.
      if (document.fonts && parent.document.fonts) {
        for (const font of parent.document.fonts) document.fonts.add(font);
      }
    } catch { /* The standalone compact URL keeps its default theme. */ }
  }
  syncDashboardTheme();
  const seasons = ['WINTER', 'SPRING', 'SUMMER', 'FALL'];
  const today = new Date();
  const current = { year: today.getFullYear(), season: seasons[Math.floor(today.getMonth() / 3)] };
  let year = current.year, season = current.season, view = 'browse', items = [], requestVersion = 0;
  let status = { entries: [], library: [], notifications: [], canAdd: false, checkedIds: [] }, selectedAnime = null;
  let statusBusy = false, matchVersion = 0, candidates = [], loading = false, statusTimer;
  const cache = new Map();
  const seenNotices = new Set();
  const cards = new Map();
  function node(tag, className, text) {
    const n = document.createElement(tag);
    if (className) n.className = className;
    if (text !== undefined) n.textContent = text;
    return n;
  }
  function key() {
    try {
      const state = JSON.parse(sessionStorage.getItem('state') || '{}');
      if (Object.hasOwn(state, 'apiSession')) return state.apiSession?.apikey || '';
      return JSON.parse(localStorage.getItem('apiSession') || '{}').apikey || '';
    } catch { return ''; }
  }
  async function api(path, body) {
    const token = key();
    if (!token) throw new Error('Sign in to Shoko, then return to Seasonal anime.');
    const response = await fetch(base + path, { method: body === undefined ? 'GET' : 'POST',
      headers: { apikey: token, ...(body === undefined ? {} : { 'Content-Type': 'application/json' }) },
      ...(body === undefined ? {} : { body: JSON.stringify(body) }) });
    const data = await response.json().catch(() => ({}));
    if (!response.ok) throw new Error(data.error || `Request failed (HTTP ${response.status}).`);
    return data;
  }
  function message(id, text, retry) {
    const box = $(id); box.replaceChildren(); box.hidden = !text;
    if (!text) return;
    box.append(node('span', '', text));
    if (retry) { const button = node('button', 'quiet', 'Retry'); button.onclick = retry; box.append(button); }
  }
  let toastTimer;
  function toast(text) { $('toast').textContent = text; $('toast').hidden = false; clearTimeout(toastTimer); toastTimer = setTimeout(() => { $('toast').hidden = true; }, 7000); }
  function link(text, url, className = '') {
    const a = node('a', className, text); a.href = url;
    if (dashboard && a.origin === location.origin) a.target = '_top';
    return a;
  }
  function shokoUrl(id) { return `${root}/webui/collection/series/${id}/episodes`; }
  function readUrl() {
    const params = new URLSearchParams(location.search);
    const y = Number(params.get('year'));
    year = Number.isInteger(y) && y >= 1940 && y <= current.year + 2 ? y : current.year;
    season = seasons.includes(params.get('season')) ? params.get('season') : current.season;
    view = params.get('view') === 'list' ? 'list' : 'browse';
  }
  function writeUrl() {
    const params = new URLSearchParams({ year, season }); if (view === 'list') params.set('view', 'list');
    if (dashboard) params.set('dashboard', '1');
    history.pushState(null, '', '?' + params);
  }
  function updateNavigation() {
    $('year').value = year;
    document.querySelectorAll('[data-season]').forEach(button => {
      const selected = button.dataset.season === season;
      button.classList.toggle('selected', selected); button.setAttribute('aria-pressed', selected);
    });
    const isList = view === 'list';
    $('season-controls').hidden = isList;
    $('page-title').textContent = isList ? 'My list' : `${season[0] + season.slice(1).toLowerCase()} ${year}`;
    $('eyebrow').textContent = isList ? 'FROM YOUR LIST TO YOUR LIBRARY' : 'SEASONAL ANIME';
    $('page-subtitle').textContent = isList ? 'Your requests, download progress and episodes ready in Shoko.' : 'Explore the season. Add a series. Follow it into your Shoko library.';
    $('search').placeholder = isList ? 'Search my list' : 'Search this season';
    $('format').disabled = isList;
    $('sort').disabled = isList;
    for (const [id, active] of [['browse-tab', !isList], ['list-tab', isList]]) {
      $(id).classList.toggle('active', active); if (active) $(id).setAttribute('aria-current', 'page'); else $(id).removeAttribute('aria-current');
    }
    $('previous').disabled = year === 1940 && season === 'WINTER';
    $('next').disabled = year === current.year + 2 && season === 'FALL';
    document.title = `${$('page-title').textContent} · Shoko Seasons`;
  }
  async function load(push = true) {
    if (push) writeUrl();
    updateNavigation();
    const version = ++requestVersion;
    message('message', '');
    if (view === 'list') { loading = false; render(); return; }
    const cacheKey = `${year}-${season}`;
    if (cache.has(cacheKey)) {
      const saved = cache.get(cacheKey); items = saved.items; loading = false;
      message('message', saved.stale ? 'Showing a saved catalog while AniList is unavailable. Try again later for updates.' : '');
      render(); refreshStatus(); return;
    }
    loading = true; items = []; render();
    try {
      const data = await api(`/catalog?year=${year}&season=${season}`);
      cache.set(cacheKey, data);
      if (version !== requestVersion) return;
      items = data.items; loading = false;
      message('message', data.stale ? 'Showing a saved catalog while AniList is unavailable. Try again later for updates.' : '');
      render(); refreshStatus();
    } catch (error) {
      if (version !== requestVersion) return;
      loading = false; render(); message('message', error.message, () => load(false));
    }
  }
  function render() {
    cards.clear(); $('catalog').replaceChildren();
    if (loading && view === 'browse') { $('catalog').append(node('div', 'loading', 'Loading seasonal anime…')); $('empty').hidden = true; $('result-count').textContent = ''; return; }
    const search = $('search').value.trim().toLowerCase();
    let visible = view === 'list' ? status.entries.map(e => ({ id: e.animeId, title: e.title, originalTitle: '', cover: e.cover, description: '', format: '', genres: [] })) : [...items];
    visible = visible.filter(a => `${a.title} ${a.originalTitle}`.toLowerCase().includes(search) && (view === 'list' || !$('format').value || a.format === $('format').value));
    if (view === 'browse' && $('sort').value === 'score') visible.sort((a, b) => (b.score || 0) - (a.score || 0));
    if (view === 'browse' && $('sort').value === 'title') visible.sort((a, b) => a.title.localeCompare(b.title));
    $('result-count').textContent = `${visible.length} ${visible.length === 1 ? 'title' : 'titles'}`;
    $('empty').hidden = visible.length > 0;
    $('empty').querySelector('h2').textContent = view === 'list' && status.entries.length === 0 ? 'Your next season starts here' : 'No titles found';
    $('empty').querySelector('p').textContent = view === 'list' && status.entries.length === 0 ? 'Browse seasonal anime and add a series to start following its downloads.' : 'Try another season or clear your filters.';
    for (const anime of visible) {
      const card = node('article', 'anime-card'); const header = node('div', 'card-header');
      header.append(node('h2', 'card-title', anime.title));
      const meta = node('div', 'card-meta');
      meta.append(node('span', '', view === 'list' ? `Sonarr season ${status.entries.find(e => e.animeId === anime.id)?.season}` : (anime.format || 'TBA').replaceAll('_', ' ')));
      if (anime.episodes) meta.append(node('span', '', `· ${anime.episodes} episodes`));
      if (anime.score) meta.append(node('span', 'score', `★ ${(anime.score / 10).toFixed(1)}`));
      header.append(meta); card.append(header);
      const body = node('div', 'card-body');
      if (anime.cover && /^https:\/\/[^/]+\.anilist\.co\//i.test(anime.cover)) {
        const img = node('img', 'poster'); img.src = anime.cover; img.alt = `${anime.title} poster`; img.loading = 'lazy'; img.referrerPolicy = 'no-referrer'; body.append(img);
      } else body.append(node('div', 'poster poster-placeholder', '◇'));
      const copy = node('div', 'card-copy');
      if (anime.studio) copy.append(node('p', 'studio', anime.studio));
      copy.append(node('p', 'synopsis', anime.description || (view === 'list' ? 'This season is on your list. Download and import status updates automatically.' : 'A synopsis has not been published yet.')));
      body.append(copy); card.append(body);
      if (anime.genres?.length) { const genres = node('div', 'genres'); anime.genres.slice(0, 4).forEach(g => genres.append(node('span', 'genre', g))); card.append(genres); }
      const footer = node('div', 'card-footer'); card.append(footer); cards.set(anime.id, { anime, footer, signature: '' });
      $('catalog').append(card);
    }
    renderStatuses();
  }
  function renderStatuses() {
    $('list-count').textContent = status.entries.length;
    for (const [id, card] of cards) {
      const entry = status.entries.find(e => e.animeId === id);
      const library = status.library.find(e => e.animeId === id);
      const libraryChecked = status.checkedIds.includes(id);
      const signature = JSON.stringify([entry, library, status.canAdd, libraryChecked]);
      if (signature === card.signature) continue;
      card.signature = signature;
      const footer = card.footer; footer.replaceChildren();
      const available = entry?.availableEpisodes || library?.availableEpisodes || 0;
      const shokoId = entry?.shokoId || library?.shokoId;
      if (entry) {
        footer.append(node('div', 'status-label' + (entry.state === 'Ready in Shoko' ? ' ready' : /failed|attention|Paused|Removed/.test(entry.state) ? ' warning' : ''), entry.state));
        if (entry.progress !== null && entry.progress !== undefined) {
          const row = node('div', 'progress-row'); const progress = node('progress'); progress.max = 100; progress.value = entry.progress; progress.setAttribute('aria-label', `${card.anime.title} active downloads`);
          row.append(progress, node('span', '', `${Math.round(entry.progress)}%`)); footer.append(row);
        }
        if (entry.message) footer.append(node('p', 'status-detail', entry.message));
      } else if (available) footer.append(node('div', 'status-label ready', `${available} episode${available === 1 ? '' : 's'} in Shoko`));
      const actions = node('div', 'card-actions');
      if (shokoId) actions.append(link(available ? 'Open episodes ↗' : 'Open series ↗', shokoUrl(shokoId), 'ready-link'));
      const retry = entry && ['Search failed', 'Adding', 'Removed from Sonarr'].includes(entry.state);
      if (!entry || retry) {
        const unsupported = ['MOVIE', 'MUSIC'].includes(card.anime.format);
        const button = node('button', 'primary add-button', shokoId ? 'Already in Shoko' : !libraryChecked ? 'Checking library…' : retry ? 'Retry request' : unsupported ? 'Not supported by Sonarr' : '+ Add to list');
        button.disabled = !!shokoId || !libraryChecked || !status.canAdd || unsupported;
        if (!status.canAdd) button.title = status.setupError || 'A Shoko administrator can add downloads.';
        button.onclick = () => openAdd(card.anime);
        actions.append(button);
      }
      const details = link(view === 'list' ? 'Details ↗' : 'MAL ↗', card.anime.malId ? `https://myanimelist.net/anime/${card.anime.malId}` : `https://anilist.co/anime/${id}`, 'details-link');
      if (!card.anime.malId) details.textContent = 'AniList ↗';
      details.target = '_blank'; details.rel = 'noopener noreferrer'; actions.append(details); footer.append(actions);
    }
  }
  async function refreshStatus() {
    syncDashboardTheme();
    if (statusBusy) return;
    statusBusy = true;
    const requestedIds = [...new Set(items.map(a => a.id).concat(status.entries.map(e => e.animeId)))];
    const ids = requestedIds.join(',');
    try {
      const result = await api('/status' + (ids ? '?ids=' + ids : ''));
      const previousIds = status.entries.map(e => e.animeId).join(',');
      status = { ...result, checkedIds: requestedIds };
      message('setup-message', result.setupError);
      message('activity-message', result.activityError);
      if (view === 'list' && previousIds !== result.entries.map(e => e.animeId).join(',')) render(); else renderStatuses();
      renderNotices();
    } catch (error) { message('activity-message', error.message, refreshStatus); }
    finally { statusBusy = false; clearTimeout(statusTimer); statusTimer = setTimeout(refreshStatus, document.hidden ? 30000 : 10000); }
  }
  function renderNotices() {
    const unread = status.notifications.filter(n => !n.read);
    $('notice-count').textContent = unread.length; $('notice-count').classList.toggle('unread', unread.length > 0);
    if (!$('notifications').hidden) {
      $('notice-items').replaceChildren();
      if (!status.notifications.length) $('notice-items').append(node('p', 'muted', 'When requested episodes arrive in Shoko, you’ll find them here.'));
      for (const notice of status.notifications) {
        const row = node('div', 'notice' + (notice.read ? '' : ' unread'));
        row.append(link(`${notice.title} · ${notice.episodes} episode${notice.episodes === 1 ? '' : 's'} ready`, shokoUrl(notice.shokoId)));
        const time = node('time', '', new Date(notice.createdAt).toLocaleString()); time.dateTime = notice.createdAt; row.append(time); $('notice-items').append(row);
      }
    }
    for (const notice of unread.slice(0, 1)) {
      if (!seenNotices.has(notice.id)) toast(`${notice.title} is ready in Shoko.`);
    }
    status.notifications.forEach(n => seenNotices.add(n.id));
  }
  async function openAdd(anime) {
    selectedAnime = anime; $('add-title').textContent = anime.title; $('match-query').value = anime.title;
    $('add-dialog').showModal(); await findMatches('');
  }
  async function findMatches(query) {
    const version = ++matchVersion;
    $('confirm-add').disabled = true; $('candidates').replaceChildren(); $('match-message').textContent = 'Finding this series in Sonarr…';
    try {
      const data = await api(`/matches?animeId=${selectedAnime.id}${query ? '&query=' + encodeURIComponent(query) : ''}`);
      if (version !== matchVersion || !$('add-dialog').open) return;
      candidates = data.candidates;
      $('match-message').textContent = data.mapped ? 'Matched by anime IDs. Check the season before adding.' : candidates.length ? 'Choose the matching series and season.' : 'No matching series found. Try its alternative title, or return after Sonarr adds the metadata.';
      candidates.forEach((candidate, index) => {
        const box = node('div', 'candidate');
        const label = node('label'); const radio = node('input'); radio.type = 'radio'; radio.name = 'candidate'; radio.value = index; radio.checked = candidates.length === 1;
        label.append(radio, document.createTextNode(`${candidate.title}${candidate.year ? ' (' + candidate.year + ')' : ''}`)); box.append(label);
        if (candidate.overview) box.append(node('p', 'overview', candidate.overview));
        const seasonLabel = node('label', 'season-choice', 'Season'); const select = node('select'); select.id = 'candidate-season-' + index;
        select.append(new Option('Choose season', ''));
        candidate.seasons.forEach(s => select.append(new Option(s === 0 ? 'Specials (season 0)' : 'Season ' + s, String(s))));
        if (candidate.suggestedSeason !== null && candidate.seasons.includes(candidate.suggestedSeason)) select.value = candidate.suggestedSeason;
        else if (candidate.seasons.filter(s => s > 0).length === 1) select.value = candidate.seasons.find(s => s > 0);
        seasonLabel.append(select); box.append(seasonLabel);
        radio.onchange = validateChoice; select.onchange = () => { radio.checked = true; validateChoice(); };
        $('candidates').append(box);
      }); validateChoice();
    } catch (error) { if (version === matchVersion) $('match-message').textContent = error.message; }
  }
  function choice() {
    const radio = document.querySelector('input[name="candidate"]:checked');
    if (!radio) return null;
    const selected = $('candidate-season-' + radio.value).value;
    return selected === '' ? null : { animeId: selectedAnime.id, tvdbId: candidates[Number(radio.value)].tvdbId, season: Number(selected) };
  }
  function validateChoice() { $('confirm-add').disabled = !choice(); }
  $('confirm-add').onclick = async () => {
    const request = choice(); if (!request) return;
    $('confirm-add').disabled = true; $('confirm-add').textContent = 'Adding…'; $('match-message').textContent = 'Sending the selected season to Sonarr…';
    try {
      const entry = await api('/list', request);
      status.entries = status.entries.filter(e => e.animeId !== entry.animeId).concat(entry);
      $('add-dialog').close(); renderStatuses(); toast(entry.state === 'Search failed' ? entry.message : `${entry.title} added to your list.`); refreshStatus();
    } catch (error) { $('match-message').textContent = error.message; }
    finally { $('confirm-add').textContent = 'Add & search'; validateChoice(); }
  };
  $('match-search').onsubmit = event => { event.preventDefault(); findMatches($('match-query').value.trim()); };
  $('cancel-add').onclick = () => $('add-dialog').close();
  $('add-dialog').addEventListener('close', () => { matchVersion++; });
  for (let y = current.year + 2; y >= 1940; y--) $('year').append(new Option(y, y));
  $('year').onchange = () => { year = Number($('year').value); load(); };
  document.querySelectorAll('[data-season]').forEach(button => button.onclick = () => { season = button.dataset.season; load(); });
  function step(delta) { const offset = seasons.indexOf(season) + delta; year += offset < 0 ? -1 : offset > 3 ? 1 : 0; season = seasons[(offset + 4) % 4]; load(); }
  $('previous').onclick = () => step(-1); $('next').onclick = () => step(1);
  $('current').onclick = () => { year = current.year; season = current.season; load(); };
  $('search').oninput = render; $('format').onchange = render; $('sort').onchange = render;
  $('browse-tab').onclick = () => { view = 'browse'; load(); };
  $('list-tab').onclick = () => { view = 'list'; load(); refreshStatus(); };
  $('notifications-button').onclick = () => { $('notifications').hidden = !$('notifications').hidden; $('notifications-button').setAttribute('aria-expanded', !$('notifications').hidden); renderNotices(); };
  $('read-notices').onclick = async () => { try { await api('/notifications/read', {}); await refreshStatus(); } catch (error) { toast(error.message); } };
  window.addEventListener('popstate', () => { readUrl(); load(false); });
  document.addEventListener('visibilitychange', () => { if (!document.hidden) refreshStatus(); });
  readUrl(); load(false); refreshStatus();
})();
