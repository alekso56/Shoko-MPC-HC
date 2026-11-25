// TVCAST-001 contract v1.5 (R23, CR-8): TV casting settings on the plugin's MPC-HC setup page.
// Implementation agents must not edit this file.
const { JSDOM } = require('jsdom');
const fs = require('node:fs');
const path = require('node:path');
const assert = require('node:assert/strict');

const html = fs.readFileSync(path.join(__dirname, '../ShokoMpc/Web/dashboard.html'), 'utf8');
const SECRET = 'typed-agent-secret-0123456789abcdefghijkl';
const SAVED = { agentUrl: 'http://cast-agent.test:17300/', lanBaseUrl: 'http://shoko-lan.test:8111', secretSet: true };
const NOT_ADMIN = 'Only Shoko administrators can change TV casting settings.';
let passed = 0;
function check(value, label) { assert(value, label); passed++; console.log('PASS: ' + label); }
const pause = () => new Promise(resolve => setTimeout(resolve, 30));

// fetch is stubbed with a minimal response object: the page may rely on ok, status and json() only,
// and passes headers as a plain object.
function page({ base = '', local = { apikey: 'key-1' }, tabState, reply }) {
  const calls = [];
  const dom = new JSDOM(html, {
    url: `http://shoko.test${base}/api/plugin/ShokoMpc/dashboard`, runScripts: 'dangerously', pretendToBeVisual: true,
    beforeParse(w) {
      if (local) w.localStorage.setItem('apiSession', JSON.stringify(local));
      if (tabState) w.sessionStorage.setItem('state', JSON.stringify(tabState));
      w.fetch = async (url, options = {}) => {
        const call = { url: String(url), method: (options.method || 'GET').toUpperCase(), headers: options.headers || {}, body: options.body };
        calls.push(call);
        const [status, body] = reply(call);
        return { ok: status >= 200 && status < 300, status, json: async () => body };
      };
    }
  });
  const d = dom.window.document;
  return { w: dom.window, d, calls, $: id => d.getElementById(id) };
}
const sent = call => JSON.parse(call.body);
const endsWithSettings = url => /^(https?:\/\/shoko\.test)?\/api\/plugin\/ShokoMpc\/cast\/settings$/.test(url);

(async () => {
  let p = page({ reply: call => call.method === 'GET' ? [200, SAVED] : [200, { ...SAVED, agentUrl: sent(call).agentUrl }] });
  await pause();
  check(p.$('cast-agent-url') && p.$('cast-lan-url') && p.$('cast-secret') && p.$('cast-save') && p.$('cast-settings-status'), 'settings-form-present');
  check(p.$('cast-secret').type === 'password' && p.$('cast-secret').getAttribute('autocomplete') === 'new-password'
    && p.$('cast-settings-status').getAttribute('role') === 'status', 'secret-field-is-write-only-password');
  check(p.calls.length === 1 && p.calls[0].method === 'GET' && endsWithSettings(p.calls[0].url) && p.calls[0].headers.apikey === 'key-1',
    'loads-settings-with-session-key');
  check(p.$('cast-agent-url').value === SAVED.agentUrl && p.$('cast-lan-url').value === SAVED.lanBaseUrl && p.$('cast-secret').value === ''
    && /leave (it )?empty/i.test(p.$('cast-secret').placeholder), 'fills-urls-and-never-the-secret');

  p.$('cast-agent-url').value = 'http://cast-agent-2.test:17300/';
  p.$('cast-save').click(); await pause();
  let put = p.calls.at(-1);
  check(put.method === 'PUT' && endsWithSettings(put.url) && put.headers.apikey === 'key-1' && sent(put).agentUrl === 'http://cast-agent-2.test:17300/'
    && sent(put).lanBaseUrl === SAVED.lanBaseUrl && !('agentSecret' in sent(put)), 'save-omits-empty-secret');
  check(p.$('cast-settings-status').textContent.trim() === 'Saved.', 'save-shows-saved');

  p.$('cast-secret').value = SECRET;
  p.$('cast-save').click(); await pause();
  put = p.calls.at(-1);
  check(sent(put).agentSecret === SECRET && p.$('cast-secret').value === '' && !p.d.documentElement.outerHTML.includes(SECRET),
    'save-sends-new-secret-then-clears-it');
  check(p.$('server').textContent === 'http://shoko.test' && p.$('download').getAttribute('href') === '/api/plugin/ShokoMpc/Windows-Setup.zip',
    'regression: setup section unchanged');
  p.w.close();

  p = page({ reply: call => call.method === 'GET' ? [200, SAVED] : [400, { error: 'Enter a valid agent URL starting with http:// or https://.' }] });
  await pause();
  p.$('cast-save').click(); await pause();
  check(p.$('cast-settings-status').textContent.trim() === 'Enter a valid agent URL starting with http:// or https://.', 'save-shows-server-error');
  p.w.close();

  p = page({ reply: () => [403, { error: NOT_ADMIN }] });
  await pause();
  check(p.$('cast-settings-status').textContent.trim() === NOT_ADMIN && p.$('cast-save').disabled
    && p.$('cast-agent-url').disabled && p.$('cast-lan-url').disabled && p.$('cast-secret').disabled, 'non-admin-sees-read-only-form');
  p.w.close();

  p = page({ local: null, reply: () => [200, SAVED] });
  await pause();
  check(p.calls.length === 0 && p.$('cast-settings-status').textContent.trim() === 'Sign in to Shoko first.' && p.$('cast-save').disabled,
    'no-session-no-request');
  p.w.close();

  p = page({ tabState: { apiSession: { apikey: 'tab-key' } }, reply: () => [200, SAVED] });
  await pause();
  check(p.calls[0].headers.apikey === 'tab-key', 'tab-session-key-takes-precedence');
  p.w.close();

  p = page({ base: '/shoko', reply: () => [200, SAVED] });
  await pause();
  check(/^(https?:\/\/shoko\.test)?\/shoko\/api\/plugin\/ShokoMpc\/cast\/settings$/.test(p.calls[0].url), 'follows-reverse-proxy-prefix');
  p.w.close();

  p = page({ reply: () => [200, { agentUrl: '', lanBaseUrl: '', secretSet: false }] });
  await pause();
  check(p.$('cast-agent-url').value === '' && !/leave (it )?empty/i.test(p.$('cast-secret').placeholder), 'unset-secret-has-no-keep-hint');
  p.w.close();

  console.log(`${passed} settings UI checks passed.`);
})().catch(error => { console.error(error); process.exitCode = 1; });
