// Drives headless Chromium over the DevTools protocol: loads the page, waits
// for the first frame, screenshots, optionally clicks, and prints the page's
// timing stats. Node >= 22 (global WebSocket). No dependencies.
// usage: node cdp-check.mjs <url> <outPrefix> [clickX clickY] [--dpr N] [--wait ms]
import { spawn } from 'node:child_process';
import { writeFileSync, mkdtempSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

const args = process.argv.slice(2);
const opt = (k, d) => { const i = args.indexOf(k); if (i < 0) return d; const v = args[i + 1]; args.splice(i, 2); return v; };
const dpr = +opt('--dpr', '1');
const wait = +opt('--wait', '3000');
const [url, out, cx, cy] = args;
const port = 9300 + Math.floor(Math.random() * 500);
const profile = mkdtempSync(join(tmpdir(), 'cdp-'));
const chrome = spawn('chromium', ['--headless=new', `--remote-debugging-port=${port}`, `--user-data-dir=${profile}`,
  '--no-first-run', '--window-size=800,600', `--force-device-scale-factor=${dpr}`, 'about:blank'], { stdio: 'ignore' });
const sleep = ms => new Promise(r => setTimeout(r, ms));
let targets;
for (let i = 0; i < 50; i++) { try { targets = await (await fetch(`http://127.0.0.1:${port}/json`)).json(); break; } catch { await sleep(100); } }
const page = targets.find(t => t.type === 'page');
const ws = new WebSocket(page.webSocketDebuggerUrl);
await new Promise(r => ws.onopen = r);
let id = 0; const pending = new Map();
ws.onmessage = m => {
  const msg = JSON.parse(m.data);
  if (msg.id && pending.has(msg.id)) { pending.get(msg.id)(msg); pending.delete(msg.id); }
  else if (msg.method === 'Runtime.consoleAPICalled') console.log('console:', msg.params.args.map(a => a.value ?? a.description).join(' '));
  else if (msg.method === 'Runtime.exceptionThrown') console.log('exception:', JSON.stringify(msg.params.exceptionDetails).slice(0, 800));
};
const send = (method, params = {}) => new Promise(r => { const i = ++id; pending.set(i, r); ws.send(JSON.stringify({ id: i, method, params })); });
const evaluate = async expr => (await send('Runtime.evaluate', { expression: expr, returnByValue: true, awaitPromise: true })).result?.result?.value;
await send('Runtime.enable'); await send('Page.enable');
await send('Network.enable'); await send('Network.setCacheDisabled', { cacheDisabled: true });
const tNav = Date.now();
await send('Page.navigate', { url });
let first = null;
for (let i = 0; i < 300 && !first; i++) { await sleep(100); first = await evaluate('window.bibuiFirstFrame'); }
console.log('wall ms to first frame (from navigate):', Date.now() - tNav);
console.log('firstFrame:', JSON.stringify(first));
const shot = async name => writeFileSync(`${out}-${name}.png`, Buffer.from((await send('Page.captureScreenshot', { format: 'png' })).result.data, 'base64'));
await sleep(500); await shot('initial');
if (cx !== undefined) {
  const mouse = (type, extra = {}) => send('Input.dispatchMouseEvent', { type, x: +cx, y: +cy, button: 'left', ...extra });
  await mouse('mouseMoved', { button: 'none' }); await sleep(300); await shot('hover');
  for (let k = 0; k < 3; k++) { await mouse('mousePressed', { clickCount: 1 }); await sleep(50); await mouse('mouseReleased', { clickCount: 1 }); await sleep(100); }
  await sleep(300); await shot('clicked');
}
await sleep(wait);
console.log('stats:', JSON.stringify(await evaluate('window.bibuiStats')));
console.log('statusline:', await evaluate("document.getElementById('stats').textContent"));
ws.close(); chrome.kill();
