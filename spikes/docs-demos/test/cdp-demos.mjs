// Drives headless Chromium over CDP against test/index.html: screenshots,
// clicks the switch, types into the text field, pastes, drags the slider,
// opens the select. Node >= 22. usage: node cdp-demos.mjs <url> <outPrefix> [--dpr N]
import { spawn } from 'node:child_process';
import { writeFileSync, mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

const args = process.argv.slice(2);
const di = args.indexOf('--dpr'); const dpr = di >= 0 ? +args.splice(di, 2)[1] : 1;
const [url, out] = args;
const port = 9300 + Math.floor(Math.random() * 500);
const profile = mkdtempSync(join(tmpdir(), 'cdp-'));
const chrome = spawn('chromium', ['--headless=new', `--remote-debugging-port=${port}`, `--user-data-dir=${profile}`,
  '--no-first-run', '--window-size=900,1100', `--force-device-scale-factor=${dpr}`, 'about:blank'], { stdio: 'ignore' });
const sleep = ms => new Promise(r => setTimeout(r, ms));
let targets;
for (let i = 0; i < 50; i++) { try { targets = await (await fetch(`http://127.0.0.1:${port}/json`)).json(); break; } catch { await sleep(100); } }
const ws = new WebSocket(targets.find(t => t.type === 'page').webSocketDebuggerUrl);
await new Promise(r => ws.onopen = r);
let id = 0; const pending = new Map();
ws.onmessage = m => {
  const msg = JSON.parse(m.data);
  if (msg.id && pending.has(msg.id)) { pending.get(msg.id)(msg); pending.delete(msg.id); }
  else if (msg.method === 'Runtime.consoleAPICalled') console.log('console:', msg.params.args.map(a => a.value ?? a.description).join(' ').slice(0, 400));
  else if (msg.method === 'Runtime.exceptionThrown') console.log('exception:', JSON.stringify(msg.params.exceptionDetails).slice(0, 600));
};
const send = (method, params = {}) => new Promise(r => { const i = ++id; pending.set(i, r); ws.send(JSON.stringify({ id: i, method, params })); });
const evaluate = async expr => (await send('Runtime.evaluate', { expression: expr, returnByValue: true, awaitPromise: true })).result?.result?.value;
await send('Runtime.enable'); await send('Page.enable');
await send('Network.enable'); await send('Network.setCacheDisabled', { cacheDisabled: true });
const t = Date.now();
await send('Page.navigate', { url });
for (let i = 0; i < 300 && !(await evaluate('window.ready')); i++) await sleep(100);
console.log('ready after', Date.now() - t, 'ms; boot', Math.round(await evaluate('window.bootMs')), 'ms');
console.log('demos():', JSON.stringify(await evaluate('window.allDemos')));
console.log('mount(unknown) =', JSON.stringify(await evaluate('window.unknown')));
await sleep(600);
const shot = async name => { writeFileSync(`${out}-${name}.png`, Buffer.from((await send('Page.captureScreenshot', { format: 'png' })).result.data, 'base64')); console.log('shot', name); };
const rect = async cid => evaluate(`(() => { const r = document.getElementById('demo-${cid}')?.getBoundingClientRect(); return r && { x: r.x, y: r.y, w: r.width, h: r.height }; })()`);
const mouse = (type, x, y, extra = {}) => send('Input.dispatchMouseEvent', { type, x, y, button: 'left', clickCount: 1, ...extra });
const click = async (x, y) => { await mouse('mouseMoved', x, y, { button: 'none' }); await sleep(60); await mouse('mousePressed', x, y); await sleep(40); await mouse('mouseReleased', x, y); await sleep(150); };
const key = async (k, code, text, vk) => {
  await send('Input.dispatchKeyEvent', { type: text ? 'keyDown' : 'rawKeyDown', key: k, code, text, windowsVirtualKeyCode: vk });
  await send('Input.dispatchKeyEvent', { type: 'keyUp', key: k, code, windowsVirtualKeyCode: vk });
  await sleep(30);
};
await shot('initial');

const sw = await rect('switch');
// The switch's track sits left of centre (the label is to its right).
if (sw) { await click(sw.x + sw.w / 2 - 52, sw.y + sw.h / 2); await sleep(400); await shot('switch-on'); }

const tf = await rect('text-field');
if (tf) {
  await click(tf.x + tf.w / 2, tf.y + tf.h / 2);
  for (const ch of 'hello@bibui') {
    const code = ch === '@' ? 'Digit2' : ch === '.' ? 'Period' : 'Key' + ch.toUpperCase();
    await key(ch, code, ch, ch.toUpperCase().charCodeAt(0));
  }
  await key('Backspace', 'Backspace', undefined, 8);
  await key('i', 'KeyI', 'i', 73);
  console.log('focused element:', await evaluate('document.activeElement?.id'));
  // A paste, as the browser fires it for Ctrl+V.
  await evaluate(`(() => { const dt = new DataTransfer(); dt.setData('text/plain', '.dev'); document.getElementById('demo-text-field').dispatchEvent(new ClipboardEvent('paste', { clipboardData: dt, bubbles: true, cancelable: true })); })()`);
  await sleep(400); await shot('text-typed');
  // Page scroll must not move when Space/arrows go to the canvas.
  console.log('scrollY after keys:', await evaluate('scrollY'));
}

const sl = await rect('slider');
if (sl) {
  // The thumb, left of centre at the example's starting value.
  const cx = sl.x + sl.w / 2 - 32, cy = sl.y + sl.h / 2 + 14;
  await mouse('mouseMoved', cx, cy, { button: 'none' }); await sleep(60);
  await mouse('mousePressed', cx, cy); await sleep(60);
  for (let dx = 0; dx <= 80; dx += 10) { await mouse('mouseMoved', cx + dx, cy); await sleep(30); }
  await mouse('mouseReleased', cx + 80, cy); await sleep(300); await shot('slider-dragged');
}

const se = await rect('select');
if (se) { await click(se.x + se.w / 2, se.y + 24 + 18); await sleep(300); await click(se.x + se.w / 2, se.y + se.h / 2); await sleep(500); await shot('select-open'); }

ws.close(); chrome.kill(); await sleep(200);
try { rmSync(profile, { recursive: true, force: true }); } catch {}
