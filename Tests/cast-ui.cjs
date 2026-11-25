// TVCAST-001 acceptance contract v1 (Web UI). Implementation agents must not edit this file.
const { JSDOM } = require('jsdom');
const fs = require('fs');
const path = require('path');
const assert = require('node:assert/strict');
const source = fs.readFileSync(path.join(__dirname, '../ShokoMpc/Web/webui.js'), 'utf8');
const dom = new JSDOM('<!doctype html><html><head></head><body><div id="row"><a href="https://anidb.net/episode/123">AniDB</a></div></body></html>', {
  url: 'http://localhost:30222/webui/collection/series/216/episodes', runScripts: 'dangerously', pretendToBeVisual: true
});
const w = dom.window;
let opened = '';
const requests = [];
let agent = { state: 'idle' };
let castReply = { ok: true, status: 202, body: { state: 'connecting' } };
w.HTMLAnchorElement.prototype.click = function () { opened = this.href; };
w.localStorage.setItem('apiSession', JSON.stringify({ apikey: 'fixture-key' }));
w.fetch = async (url, options = {}) => {
  const method = (options.method || 'GET').toUpperCase();
  requests.push({ url, method, options });
  const reply = (ok, status, body) => ({ ok, status, json: async () => body });
  if (url.includes('Playlist/Generate?playlist=e123')) return reply(true, 200, [{ Parts: [{ ID: 9 }] }]);
  if (url.includes('Playlist/Generate?playlist=e456')) return reply(true, 200, [{ Parts: [{ ID: 9 }, { ID: 10 }] }]);
  if (url === '/api/plugin/ShokoMpc/cast' && method === 'POST') return reply(castReply.ok, castReply.status, castReply.body);
  if (url === '/api/plugin/ShokoMpc/cast' && method === 'GET') return reply(true, 200, agent);
  if (url === '/api/plugin/ShokoMpc/cast' && method === 'DELETE') { agent = { state: 'stopping' }; return reply(true, 202, agent); }
  if (url.includes('/launch')) return reply(true, 200, { ticket: 'a'.repeat(64) });
  return reply(false, 404, { error: 'unexpected request ' + method + ' ' + url });
};
Object.defineProperty(w.document, 'currentScript', { value: { src: 'http://localhost:30222/api/plugin/ShokoMpc/webui.js' }, configurable: true });
w.eval(source);
const wait = (ms = 80) => new Promise(resolve => setTimeout(resolve, ms));
const pollWait = () => wait(2300);
const tv = () => w.document.querySelectorAll('.shoko-cast-play');
const notice = () => w.document.querySelector('#shoko-cast-notice');
const casts = method => requests.filter(r => r.url === '/api/plugin/ShokoMpc/cast' && r.method === method);
const buttonIn = (text) => [...notice().querySelectorAll('button')].find(b => b.textContent.trim() === text);
let passed = 0;
function check(label, fn) { fn(); console.log('PASS: ' + label); passed++; }

(async () => {
  await wait();
  check('tv-button-once-per-link', () => {
    assert.equal(tv().length, 1);
    const button = tv()[0];
    assert.equal(button.textContent, '▶ TV');
    assert.equal(button.getAttribute('aria-label'), 'Play episode 123 on the TV');
    assert.equal(button.previousElementSibling.className, 'shoko-mpc-play');
  });

  tv()[0].click(); await wait();
  check('tv-click-posts-fileids', () => {
    const post = casts('POST');
    assert.equal(post.length, 1);
    assert.deepEqual(JSON.parse(post[0].options.body), { fileIds: [9] });
    assert.equal(post[0].options.headers.apikey, 'fixture-key');
    assert.equal(post[0].options.headers['Content-Type'], 'application/json');
  });
  check('tv-response-has-no-token', () => {
    assert.equal(opened, '');
    assert.deepEqual(Object.keys(JSON.parse(casts('POST')[0].options.body)), ['fileIds']);
  });
  check('no-playing-before-agent-says', () => {
    assert.equal(notice().getAttribute('role'), 'status');
    assert.match(notice().textContent, /Connecting to the TV…/);
    assert.doesNotMatch(notice().textContent, /Playing/);
    assert.ok(buttonIn('Stop TV'));
  });

  agent = { state: 'playing', title: 'Show - 01', positionSeconds: 10 };
  await pollWait();
  check('status-text-per-state: playing', () => {
    assert.ok(casts('GET').length >= 1);
    assert.equal(casts('GET')[0].options.headers.apikey, 'fixture-key');
    assert.match(notice().textContent, /Playing "Show - 01" on the TV/);
  });

  tv()[0].click(); await wait();
  check('tv-while-playing-shows-replace', () => {
    assert.equal(casts('POST').length, 2);
    assert.match(notice().textContent, /Switching the TV to this episode…/);
  });

  agent = { state: 'error', error: "The TV didn't answer. Put it in screen-mirroring mode and try again." };
  await pollWait();
  check('status-text-per-state: error', () => {
    assert.match(notice().textContent, /The TV didn't answer\. Put it in screen-mirroring mode and try again\./);
  });

  agent = { state: 'playing', title: 'Show - 01', positionSeconds: 30 };
  await pollWait();
  buttonIn('Stop TV').click(); await wait();
  check('stop-sends-delete', () => {
    assert.equal(casts('DELETE').length, 1);
    assert.equal(casts('DELETE')[0].options.headers.apikey, 'fixture-key');
  });

  agent = { state: 'idle' };
  await pollWait();
  check('status-text-per-state: stopped', () => {
    assert.match(notice().textContent, /Stopped\./);
    assert.ok(buttonIn('Dismiss'));
  });

  buttonIn('Dismiss').click();
  const getsAfterDismiss = casts('GET').length;
  await pollWait();
  check('polling-stops-after-dismiss', () => {
    assert.equal(notice(), null);
    assert.equal(casts('GET').length, getsAfterDismiss);
  });

  castReply = { ok: false, status: 502, body: { error: 'The TV caster is not responding.' } };
  tv()[0].click(); await wait();
  check('server-error-text-shown', () => {
    assert.match(notice().textContent, /The TV caster is not responding\./);
    assert.equal(tv()[0].disabled, false);
    assert.equal(tv()[0].textContent, '▶ TV');
  });
  buttonIn('Dismiss').click();
  castReply = { ok: true, status: 202, body: { state: 'connecting' } };

  const postsBeforeMultipart = casts('POST').length;
  const anchor = w.document.querySelector('#row a'); anchor.href = 'https://anidb.net/episode/456';
  await wait();
  check('tv-button-survives-rerender', () => {
    assert.equal(tv().length, 1);
    assert.equal(tv()[0].getAttribute('aria-label'), 'Play episode 456 on the TV');
  });
  tv()[0].click(); await wait();
  check('multipart-message', () => {
    assert.match(notice().textContent, /Multi-part episodes can't play on the TV yet\./);
    assert.equal(casts('POST').length, postsBeforeMultipart);
  });
  buttonIn('Dismiss').click();

  w.localStorage.removeItem('apiSession');
  tv()[0].click(); await wait();
  check('signed-out-asks-to-sign-in', () => {
    assert.match(notice().textContent, /Sign in to Shoko first\./);
    assert.equal(casts('POST').length, postsBeforeMultipart);
  });
  buttonIn('Dismiss').click();

  w.eval(source); await wait();
  check('no-duplicate-buttons-after-reload', () => {
    assert.equal(tv().length, 1);
    assert.equal(w.document.querySelectorAll('.shoko-mpc-play').length, 1);
  });

  console.log(passed + ' cast UI checks passed.');
  w.dispatchEvent(new w.Event('pagehide')); w.close();
})().catch(error => { console.error(error); process.exitCode = 1; w.close(); });
