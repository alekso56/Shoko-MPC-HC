// TVCAST-001 contract v2.1 (CR-11 stage 3, R29): the cast target dropdown. Implementation agents must not edit this file.
const { JSDOM } = require('jsdom');
const fs = require('fs');
const path = require('path');
const assert = require('node:assert/strict');
const source = fs.readFileSync(path.join(__dirname, '../ShokoMpc/Web/webui.js'), 'utf8');
const KONTOR = 'cast:uuid:00000000-0000-4000-8000-000000000001';
const SAMSUNG = 'dlna:uuid:0f4c3a2e-1b2c-4d5e-8f90-aabbccddeeff';
const TARGETS = [
  { id: 'miracast', name: 'Wi-Fi Direct display', kind: 'miracast', available: true },
  { id: KONTOR, name: 'Office streamer', kind: 'cast', available: true },
  { id: SAMSUNG, name: 'Living room TV', kind: 'dlna', available: false },
];

// Contract v2.1 (CR-11 stage 3 test defect): every fixture window is closed at the end, so status polling can't keep Node alive.
const windows = [];
function page({ remembered, targetsReply } = {}) {
  const dom = new JSDOM('<!doctype html><html><head></head><body><div id="row"><a href="https://anidb.net/episode/123">AniDB</a></div></body></html>', {
    url: 'http://localhost:30222/webui/collection/series/216/episodes', runScripts: 'dangerously', pretendToBeVisual: true
  });
  const w = dom.window;
  windows.push(w);
  const requests = [];
  let agent = { state: 'idle' };
  w.localStorage.setItem('apiSession', JSON.stringify({ apikey: 'fixture-key' }));
  if (remembered !== undefined) w.localStorage.setItem('shoko-cast-target', remembered);
  w.fetch = async (url, options = {}) => {
    const method = (options.method || 'GET').toUpperCase();
    requests.push({ url, method, options });
    const reply = (ok, status, body) => ({ ok, status, json: async () => body });
    if (url.includes('Playlist/Generate?playlist=e123')) return reply(true, 200, [{ Parts: [{ ID: 9 }] }]);
    if (url === '/api/plugin/ShokoMpc/cast/targets' && method === 'GET') {
      return targetsReply ? reply(...targetsReply) : reply(true, 200, TARGETS);
    }
    if (url === '/api/plugin/ShokoMpc/cast' && method === 'POST') {
      agent = { state: 'connecting', title: 'Show - 01.mkv', target: JSON.parse(options.body).targetId };
      return reply(true, 202, { state: 'connecting' });
    }
    if (url === '/api/plugin/ShokoMpc/cast' && method === 'GET') return reply(true, 200, agent);
    if (url === '/api/plugin/ShokoMpc/cast' && method === 'DELETE') { agent = { state: 'stopping' }; return reply(true, 202, agent); }
    return reply(false, 404, { error: 'unexpected request ' + method + ' ' + url });
  };
  Object.defineProperty(w.document, 'currentScript', { value: { src: 'http://localhost:30222/api/plugin/ShokoMpc/webui.js' }, configurable: true });
  w.eval(source);
  const d = w.document;
  return {
    w, d, requests,
    setAgent: value => { agent = value; },
    play: () => d.querySelector('.shoko-cast-play'),
    menuButton: () => d.querySelector('.shoko-cast-menu'),
    menu: () => d.querySelector('#shoko-cast-targets'),
    items: () => [...d.querySelectorAll('#shoko-cast-targets [role="menuitemradio"]')],
    notice: () => d.querySelector('#shoko-cast-notice'),
    calls: (url, method) => requests.filter(r => r.url === url && r.method === method),
  };
}
const wait = (ms = 80) => new Promise(resolve => setTimeout(resolve, ms));
let passed = 0;
function check(label, fn) { fn(); console.log('PASS: ' + label); passed++; }

(async () => {
  let p = page();
  await wait();
  check('menu-button-beside-tv-button', () => {
    assert.equal(p.play().textContent, '▶ TV');
    const menuButton = p.menuButton();
    assert.ok(menuButton, 'a ▾ button exists');
    assert.equal(menuButton.textContent, '▾');
    assert.equal(menuButton.previousElementSibling, p.play());
    assert.equal(menuButton.getAttribute('aria-label'), 'Choose a TV');
    assert.equal(menuButton.getAttribute('aria-haspopup'), 'menu');
    assert.equal(menuButton.getAttribute('aria-expanded'), 'false');
  });
  check('no-target-request-on-page-load', () => {
    assert.equal(p.calls('/api/plugin/ShokoMpc/cast/targets', 'GET').length, 0);
  });

  p.menuButton().click(); await wait();
  check('menu-lists-targets-when-opened', () => {
    const call = p.calls('/api/plugin/ShokoMpc/cast/targets', 'GET');
    assert.equal(call.length, 1);
    assert.equal(call[0].options.headers.apikey, 'fixture-key');
    assert.equal(p.menu().getAttribute('role'), 'menu');
    assert.equal(p.menuButton().getAttribute('aria-expanded'), 'true');
    assert.deepEqual(p.items().map(i => i.textContent.trim()), ['Wi-Fi Direct display', 'Office streamer', 'Living room TV (asleep)']);
    assert.deepEqual(p.items().map(i => i.getAttribute('aria-checked')), ['true', 'false', 'false']);
  });

  p.items()[1].click(); await wait();
  check('choosing-a-target-renames-the-button-and-remembers-it', () => {
    assert.equal(p.menu(), null);
    assert.equal(p.menuButton().getAttribute('aria-expanded'), 'false');
    assert.equal(p.play().textContent, '▶ Office streamer');
    assert.equal(p.play().getAttribute('aria-label'), 'Play episode 123 on Office streamer');
    assert.deepEqual(JSON.parse(p.w.localStorage.getItem('shoko-cast-target')), { id: KONTOR, name: 'Office streamer' });
  });

  p.play().click(); await wait();
  check('cast-sends-the-chosen-target', () => {
    const post = p.calls('/api/plugin/ShokoMpc/cast', 'POST');
    assert.equal(post.length, 1);
    assert.deepEqual(JSON.parse(post[0].options.body), { fileIds: [9], targetId: KONTOR });
  });
  check('notice-names-the-target-while-connecting', () => {
    assert.match(p.notice().textContent, /Connecting to Office streamer…/);
  });
  p.setAgent({ state: 'playing', title: 'Show - 01.mkv', positionSeconds: 12, target: KONTOR });
  await wait(2300);
  check('notice-names-the-target-while-playing', () => {
    assert.match(p.notice().textContent, /Playing "Show - 01\.mkv" on Office streamer/);
  });

  p = page({ remembered: JSON.stringify({ id: KONTOR, name: 'Office streamer' }) });
  await wait();
  check('remembered-target-survives-a-reload', () => {
    assert.equal(p.play().textContent, '▶ Office streamer');
    assert.equal(p.calls('/api/plugin/ShokoMpc/cast/targets', 'GET').length, 0);
  });
  p.d.getElementById('row').insertAdjacentHTML('beforeend', '<a href="https://anidb.net/episode/124">AniDB 2</a>');
  await wait();
  check('new-rows-use-the-remembered-target', () => {
    const buttons = [...p.d.querySelectorAll('.shoko-cast-play')];
    assert.equal(buttons.length, 2);
    assert.ok(buttons.every(b => b.textContent === '▶ Office streamer'));
    assert.equal(p.d.querySelectorAll('.shoko-cast-menu').length, 2);
  });

  p.menuButton().click(); await wait();
  p.items()[0].click(); await wait();
  p.play().click(); await wait();
  check('choosing-the-wifi-direct-display-sends-miracast', () => {
    assert.equal(p.play().textContent, '▶ Wi-Fi Direct display');
    assert.deepEqual(JSON.parse(p.calls('/api/plugin/ShokoMpc/cast', 'POST').at(-1).options.body), { fileIds: [9], targetId: 'miracast' });
  });

  for (const bad of ['{not json', JSON.stringify({ id: '../x', name: 'X' }), JSON.stringify({ id: KONTOR, name: '' }), JSON.stringify(['x'])]) {
    p = page({ remembered: bad });
    await wait();
    assert.equal(p.play().textContent, '▶ TV', 'invalid remembered value ' + bad);
    p.play().click(); await wait();
    assert.deepEqual(JSON.parse(p.calls('/api/plugin/ShokoMpc/cast', 'POST')[0].options.body), { fileIds: [9] });
  }
  check('invalid-remembered-targets-are-ignored', () => {});

  p = page({ targetsReply: [false, 502, { error: 'The TV caster is not responding.' }] });
  await wait();
  p.menuButton().click(); await wait();
  check('menu-shows-the-agent-error', () => {
    assert.ok(p.menu());
    assert.match(p.menu().textContent, /The TV caster is not responding\./);
    assert.equal(p.items().length, 0);
  });

  p = page({ targetsReply: [true, 200, [{ id: KONTOR, name: '<img src=x onerror=alert(1)>', kind: 'cast', available: true }]] });
  await wait();
  p.menuButton().click(); await wait();
  check('target-names-are-text-not-html', () => {
    assert.equal(p.d.querySelectorAll('#shoko-cast-targets img').length, 0);
    assert.equal(p.items()[0].textContent.trim(), '<img src=x onerror=alert(1)>');
  });

  p = page();
  await wait();
  p.menuButton().click(); await wait();
  p.d.dispatchEvent(new p.w.KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
  await wait();
  check('escape-closes-the-menu', () => {
    assert.equal(p.menu(), null);
    assert.equal(p.menuButton().getAttribute('aria-expanded'), 'false');
  });
  p.menuButton().click(); await wait();
  p.d.body.click(); await wait();
  check('clicking-elsewhere-closes-the-menu', () => {
    assert.equal(p.menu(), null);
  });

  console.log(`${passed} target UI checks passed.`);
})().catch(error => { console.error(error); process.exitCode = 1; }).finally(() => {
  for (const w of windows) { w.dispatchEvent(new w.Event('pagehide')); w.close(); }
});
