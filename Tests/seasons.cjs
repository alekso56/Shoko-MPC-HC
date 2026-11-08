const { JSDOM } = require('jsdom');
const fs = require('node:fs');
const path = require('node:path');
const assert = require('node:assert/strict');
const directory = path.join(__dirname, '../ShokoSeasons/Web');
const dom = new JSDOM(fs.readFileSync(path.join(directory, 'index.html'), 'utf8'), {
  url: 'http://localhost/api/plugin/ShokoSeasons/?year=2026&season=FALL', runScripts: 'outside-only', pretendToBeVisual: true
});
const w = dom.window, d = w.document, requests = [];
const pause = () => new Promise(resolve => setTimeout(resolve, 30));
let pendingWinter, pendingSpring, entries = [], notifications = [], library = [];
const anime = { id: 10, malId: 20, title: '<img src=x onerror=alert(1)> Anime', originalTitle: 'Original', description: '<script>evil()</script>', cover: '', format: 'TV', genres: ['Action'], score: 85, episodes: 12 };
w.localStorage.setItem('apiSession', JSON.stringify({ apikey: 'fixture' }));
w.HTMLDialogElement.prototype.showModal = function () { this.open = true; };
w.HTMLDialogElement.prototype.close = function () { this.open = false; this.dispatchEvent(new w.Event('close')); };
w.fetch = async (url, options) => {
  requests.push({ url, options });
  let data;
  if (url.includes('/catalog')) {
    if (url.includes('WINTER')) data = await new Promise(resolve => { pendingWinter = resolve; });
    else if (url.includes('SPRING')) data = await new Promise(resolve => { pendingSpring = resolve; });
    else data = { items: [anime], stale: false };
  } else if (url.includes('/status')) data = { entries, notifications, library, canAdd: true, userId: 7 };
  else if (url.includes('/matches')) data = { mapped: true, candidates: [{ tvdbId: 100, title: 'Matched anime', year: 2026, overview: 'Series overview', seasons: [1, 2, 3], suggestedSeason: 2 }] };
  else if (url.endsWith('/notifications/read')) { notifications = notifications.map(n => ({ ...n, read: true })); data = {}; }
  else if (url.endsWith('/list')) { data = { animeId: 10, title: anime.title, season: 2, state: 'Downloading', progress: 45, availableEpisodes: 0 }; entries = [data]; }
  else throw new Error(url);
  return { ok: true, json: async () => data };
};
let passed = 0;
function check(value, label) { assert(value, label); passed++; console.log('PASS: ' + label); }
(async () => {
  w.eval(fs.readFileSync(path.join(directory, 'app.js'), 'utf8')); await pause();
  check(d.querySelectorAll('.anime-card').length === 1 && d.getElementById('page-title').textContent === 'Fall 2026', 'catalog loads selected deep-link season');
  check(!d.querySelector('.anime-card script') && !d.querySelector('.anime-card img'), 'untrusted metadata rendered as text');
  check(requests.every(r => r.options.headers.apikey === 'fixture' && !r.url.includes('fixture')), 'API authentication stays in headers');
  d.querySelector('[data-season="WINTER"]').click(); await pause();
  d.querySelector('[data-season="SPRING"]').click(); await pause();
  pendingSpring({ items: [{ ...anime, title: 'Spring title' }] }); await pause();
  pendingWinter({ items: [{ ...anime, title: 'Stale winter title' }] }); await pause();
  check(d.querySelector('.card-title').textContent === 'Spring title', 'late season response cannot replace current season');
  d.querySelector('[data-season="FALL"]').click(); await pause();
  check(requests.filter(r => r.url.includes('/catalog') && r.url.includes('FALL')).length === 1, 'returning to season uses browser cache');
  library = [{ animeId: 10, shokoId: 44, availableEpisodes: 12 }];
  d.dispatchEvent(new w.Event('visibilitychange')); await pause();
  check(d.querySelector('.add-button').disabled && d.querySelector('.add-button').textContent === 'Already in Shoko', 'owned anime shows a disabled Already in Shoko button');
  d.querySelector('.add-button').click(); await pause();
  check(!d.getElementById('add-dialog').open && !requests.some(r => r.url.includes('/matches')), 'owned anime cannot open the request dialog');
  library = [{ animeId: 10, shokoId: 44, availableEpisodes: 0 }];
  d.dispatchEvent(new w.Event('visibilitychange')); await pause();
  check(d.querySelector('.add-button').disabled, 'existing empty Shoko series remains blocked');
  library = []; d.dispatchEvent(new w.Event('visibilitychange')); await pause();
  d.querySelector('.add-button').click(); await pause();
  check(d.getElementById('add-dialog').open && d.getElementById('candidate-season-0').value === '2', 'add dialog suggests mapped TVDB season');
  check(!requests.some(r => r.url.endsWith('/list')), 'opening the match dialog starts no downloads');
  d.getElementById('confirm-add').click(); await pause();
  const add = requests.find(r => r.url.endsWith('/list'));
  check(JSON.stringify(JSON.parse(add.options.body)) === JSON.stringify({ animeId: 10, tvdbId: 100, season: 2 }), 'confirmation requests exactly the selected title and season');
  check(d.querySelector('progress').value === 45 && d.querySelector('.status-label').textContent === 'Downloading', 'actual download progress shown');
  d.getElementById('list-tab').click(); await pause();
  check(d.getElementById('page-title').textContent === 'My list' && d.getElementById('season-controls').hidden, 'list view retains request and hides season picker');
  notifications = [{ id: 'notice-1', animeId: 10, title: anime.title, shokoId: 44, episodes: 2, createdAt: '2026-11-08T12:00:00Z', read: false }];
  entries = [{ ...entries[0], state: 'Ready in Shoko', progress: null, shokoId: 44, availableEpisodes: 2 }];
  w.document.dispatchEvent(new w.Event('visibilitychange')); await pause();
  check(d.querySelector('.ready-link').href.endsWith('/webui/collection/series/44/episodes') && d.getElementById('notice-count').textContent === '1', 'ready event links to Shoko episodes and updates unread count');
  d.getElementById('notifications-button').click(); await pause();
  check(d.querySelectorAll('.notice').length === 1 && !d.getElementById('notifications').hidden, 'notification inbox displays persisted event');
  d.getElementById('read-notices').click(); await pause();
  check(d.getElementById('notice-count').textContent === '0', 'mark read updates notification count');
  w.sessionStorage.setItem('state', JSON.stringify({ apiSession: { apikey: '' } }));
  const before = requests.length;
  w.document.dispatchEvent(new w.Event('visibilitychange')); await pause();
  check(requests.length === before && d.getElementById('activity-message').textContent.includes('Sign in'), 'sign-out does not reuse stale remembered credentials');
  console.log(`${passed} seasonal DOM checks passed.`);
})().catch(error => { console.error(error); process.exitCode = 1; }).finally(() => w.close());
