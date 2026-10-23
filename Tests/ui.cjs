const { JSDOM } = require('jsdom');
const fs = require('fs');
const assert = require('node:assert/strict');
const source = fs.readFileSync(require('path').join(__dirname, '../ShokoMpc/Web/webui.js'), 'utf8');
const dom = new JSDOM('<!doctype html><html><head></head><body><div id="row"><a href="https://anidb.net/episode/123">AniDB</a></div></body></html>', {
  url: 'http://localhost:30222/webui/collection/series/216/episodes', runScripts: 'dangerously', pretendToBeVisual: true
});
const w = dom.window;
let opened = ''; const requests = [];
w.HTMLAnchorElement.prototype.click = function () { opened = this.href; };
w.localStorage.setItem('apiSession', JSON.stringify({ apikey: 'fixture-key' }));
w.fetch = async (path, options) => {
  requests.push({ path, options });
  return { ok: true, json: async () => path.includes('Playlist/Generate')
    ? [{ Parts: [{ ID: 9 }, { ID: 10 }] }] : { ticket: 'a'.repeat(64) } };
};
const script = w.document.createElement('script');
Object.defineProperty(w.document, 'currentScript', { value: { src: 'http://localhost:30222/api/plugin/ShokoMpc/webui.js' }, configurable: true });
w.eval(source);
const wait = () => new Promise(resolve => setTimeout(resolve, 60));
(async () => {
  await wait(); assert.equal(w.document.querySelectorAll('.shoko-mpc-play').length, 1);
  w.document.querySelector('.shoko-mpc-play').click(); await wait();
  assert.match(opened, /^shoko-mpc:\/\/play\/\?server=/);
  assert(!opened.includes('fixture-key'));
  assert.equal(requests[0].path, '/api/v3/Playlist/Generate?playlist=e123');
  assert.deepEqual(JSON.parse(requests[1].options.body).fileIds, [9, 10]);
  const anchor = w.document.querySelector('#row a'); anchor.href = 'https://anidb.net/episode/456';
  await wait(); assert.equal(w.document.querySelectorAll('.shoko-mpc-play').length, 1);
  w.document.querySelector('.shoko-mpc-play').click(); await wait();
  assert.equal(requests[2].path, '/api/v3/Playlist/Generate?playlist=e456');
  w.localStorage.removeItem('apiSession'); w.document.querySelector('.shoko-mpc-play').click(); await wait();
  assert.match(w.document.querySelector('#shoko-mpc-notice').textContent, /Sign in/);
  assert.equal(w.document.querySelector('.shoko-mpc-play').disabled, false);
  w.eval(source); await wait(); assert.equal(w.document.querySelectorAll('.shoko-mpc-play').length, 1);
  console.log('10 DOM checks passed: episode mapping, multipart playback, no API key in links, virtual row reuse, sign-out, duplicate prevention.');
  w.dispatchEvent(new w.Event('pagehide')); w.close();
})().catch(error => { console.error(error); process.exitCode = 1; w.close(); });
