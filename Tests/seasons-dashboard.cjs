const { JSDOM } = require('jsdom');
const fs = require('node:fs');
const path = require('node:path');
const assert = require('node:assert/strict');
const source = fs.readFileSync(path.join(__dirname, '../ShokoSeasons/Web/webui.js'), 'utf8');
const dom = new JSDOM('<script id="shoko-seasons-script" src="/api/plugin/ShokoSeasons/webui.js"></script><div id="route"></div>', {
  url: 'http://localhost/webui/dashboard', runScripts: 'outside-only', pretendToBeVisual: true
});
const w = dom.window, d = w.document;
const pause = () => new Promise(resolve => setTimeout(resolve, 50));
let passed = 0;
function check(value, label) { assert(value, label); passed++; console.log('PASS: ' + label); }
(async () => {
  w.eval(source);
  check(!d.querySelector('iframe'), 'card waits for the asynchronously mounted dashboard');
  d.getElementById('route').innerHTML = '<div class="react-grid-layout"><div id="built-in">Existing card</div></div>';
  await pause();
  check(d.querySelector('#shoko-seasons-dashboard + .react-grid-layout') && d.getElementById('built-in').textContent === 'Existing card', 'seasonal card mounts outside the built-in grid without changing its cards');
  check(d.querySelector('iframe').src.endsWith('/api/plugin/ShokoSeasons/?dashboard=1'), 'dashboard embeds the authenticated catalog in compact mode');
  d.querySelector('#shoko-seasons-dashboard button').click(); await pause();
  check(!d.querySelector('iframe') && w.localStorage.getItem('shoko-seasons-dashboard-hidden') === 'true', 'hiding removes the frame and persists the preference');
  w.history.pushState(null, '', '/webui/collection');
  d.getElementById('route').replaceChildren(); await pause();
  check(!d.getElementById('shoko-seasons-dashboard'), 'card leaves with the dashboard on SPA navigation');
  w.history.pushState(null, '', '/webui/dashboard');
  d.getElementById('route').innerHTML = '<div class="react-grid-layout"></div>'; await pause();
  check(!d.querySelector('iframe') && d.querySelector('#shoko-seasons-dashboard button').textContent === 'Add seasonal anime card', 'returning to dashboard keeps the card hidden and offers Add');
  d.querySelector('#shoko-seasons-dashboard button').click(); await pause();
  d.getElementById('route').append(d.createElement('div')); await pause();
  check(d.querySelectorAll('#shoko-seasons-dashboard').length === 1 && d.querySelectorAll('iframe').length === 1 && w.localStorage.getItem('shoko-seasons-dashboard-hidden') === 'false', 'adding restores one card without duplicate frames after mutations');
  console.log(`${passed} dashboard integration checks passed.`);
})().catch(error => { console.error(error); process.exitCode = 1; }).finally(() => { w.dispatchEvent(new w.Event('pagehide')); w.close(); });
