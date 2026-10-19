// The browser route, end to end: serves a published samples/counter-browser,
// opens it in headless Chromium at devicePixelRatio 1, 1.25 and 2, and checks
// what is on the canvas: the backing store is the canvas's size in device
// pixels, the label is drawn (text), the button lights under the pointer, and
// clicks change the label.
//
//   dotnet publish samples/counter-browser -c Release -o out/counter-browser
//   node tests/browser-smoke/smoke.mjs out/counter-browser/wwwroot
//
// Needs Node 22 or later (its WebSocket), and Chromium or Chrome: CHROME names
// the binary, or one of chromium, chromium-browser, google-chrome is on PATH.
// With neither it prints SKIP and exits 0. It exits 1 on any failure.
// SMOKE_SCREENSHOTS=DIR saves what each ratio ended on, as DIR/counter-<ratio>.png.
import { spawn, spawnSync } from 'node:child_process';
import { createServer } from 'node:http';
import { readFile, writeFile, mkdir, mkdtemp, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join, extname, resolve, sep } from 'node:path';

const root = resolve(process.argv[2] ?? '');
const ratios = [1, 1.25, 2];
const sleep = ms => new Promise(r => setTimeout(r, ms));

const chrome = [process.env.CHROME, 'chromium', 'chromium-browser', 'google-chrome', 'google-chrome-stable']
  .filter(Boolean).find(b => spawnSync(b, ['--version'], { stdio: 'ignore' }).status === 0);
if (!chrome) { console.log('SKIP: no Chromium or Chrome (set CHROME)'); process.exit(0); }
if (!process.argv[2]) { console.error('usage: node smoke.mjs <published wwwroot>'); process.exit(2); }

// A static server for the published app: WebAssembly needs its MIME type.
const types = { '.html': 'text/html', '.js': 'text/javascript', '.mjs': 'text/javascript', '.wasm': 'application/wasm',
  '.json': 'application/json', '.dat': 'application/octet-stream', '.css': 'text/css' };
const server = createServer(async (req, res) => {
  const path = resolve(root, '.' + decodeURIComponent(new URL(req.url, 'http://x').pathname).replace(/\/$/, '/index.html'));
  if (path !== root && !path.startsWith(root + sep)) { res.writeHead(403).end(); return; }
  try {
    const body = await readFile(path);
    res.writeHead(200, { 'content-type': types[extname(path)] ?? 'application/octet-stream', 'cache-control': 'no-store' }).end(body);
  } catch { res.writeHead(404).end(); }
});
await new Promise(r => server.listen(0, '127.0.0.1', r));
const url = `http://127.0.0.1:${server.address().port}/`;

let failures = 0;
const fail = (what) => { console.log(`  [FAIL] ${what}`); failures++; };
const pass = (what) => console.log(`  [PASS] ${what}`);

for (const ratio of ratios) {
  console.log(`devicePixelRatio ${ratio}`);
  const profile = await mkdtemp(join(tmpdir(), 'bibui-smoke-'));
  const port = 9300 + Math.floor(Math.random() * 600);
  // CI runners (Ubuntu 24.04 on GitHub) forbid the unprivileged user
  // namespaces Chromium's sandbox needs, and Chromium then exits at once. The
  // page under test is the sample, served from this checkout, so in CI the
  // sandbox is left off; on a desktop it stays on.
  const sandbox = process.env.CI ? ['--no-sandbox'] : [];
  const browser = spawn(chrome, ['--headless=new', ...sandbox, `--remote-debugging-port=${port}`, `--user-data-dir=${profile}`,
    '--no-first-run', '--no-default-browser-check', '--window-size=640,360', `--force-device-scale-factor=${ratio}`,
    'about:blank'], { stdio: ['ignore', 'ignore', 'pipe'] });
  let stderr = '';
  browser.stderr.on('data', d => { stderr += d; });
  try {
    // The first start on a cold machine can take well over ten seconds (CI's
    // did), so wait up to 30 for the debugging port to list a page.
    let targets;
    for (let i = 0; i < 300 && !targets; i++) {
      try {
        const list = await (await fetch(`http://127.0.0.1:${port}/json`)).json();
        if (list.some(t => t.type === 'page')) targets = list; else await sleep(100);
      } catch { await sleep(100); }
    }
    if (!targets) throw new Error(`${chrome} did not start (exit ${browser.exitCode}): ${stderr.trim().split('\n').slice(-5).join(' | ')}`);
    const ws = new WebSocket(targets.find(t => t.type === 'page').webSocketDebuggerUrl);
    await new Promise(r => ws.onopen = r);
    let id = 0;
    const pending = new Map(), errors = [];
    ws.onmessage = m => {
      const msg = JSON.parse(m.data);
      if (msg.id && pending.has(msg.id)) { pending.get(msg.id)(msg); pending.delete(msg.id); }
      else if (msg.method === 'Runtime.exceptionThrown') errors.push(JSON.stringify(msg.params.exceptionDetails).slice(0, 300));
      else if (msg.method === 'Runtime.consoleAPICalled' && msg.params.type === 'error')
        errors.push(msg.params.args.map(a => a.value ?? a.description).join(' ').slice(0, 300));
    };
    const send = (method, params = {}) => new Promise(r => { const i = ++id; pending.set(i, r); ws.send(JSON.stringify({ id: i, method, params })); });
    const evaluate = async expr => (await send('Runtime.evaluate', { expression: expr, returnByValue: true, awaitPromise: true })).result?.result?.value;
    await send('Runtime.enable');
    await send('Page.enable');
    await send('Page.navigate', { url });

    // A region of the canvas, in CSS pixels: how many pixels are not the
    // background, how many distinct values they take, and a hash of them.
    const region = (x, y, w, h) => evaluate(`(() => {
      const c = document.getElementById('counter'), r = devicePixelRatio;
      const d = c.getContext('2d').getImageData(Math.round(${x} * r), Math.round(${y} * r), Math.round(${w} * r), Math.round(${h} * r)).data;
      const bg = [d[0], d[1], d[2]]; let ink = 0, hash = 0; const levels = new Set();
      for (let i = 0; i < d.length; i += 4) {
        if (d[i] !== bg[0] || d[i + 1] !== bg[1] || d[i + 2] !== bg[2]) { ink++; levels.add(d[i]); }
        hash = (hash * 31 + d[i] + d[i + 1] * 7 + d[i + 2] * 13) | 0;
      }
      return { ink, levels: levels.size, hash };
    })()`);
    const label = () => region(0, 0, 160, 18);
    const button = () => region(2, 20, 80, 26);

    let first = null;
    for (let i = 0; i < 150 && !(first?.ink > 0); i++) { await sleep(100); first = await label(); }
    const size = await evaluate(`(() => { const c = document.getElementById('counter'), b = c.getBoundingClientRect();
      return { w: c.width, h: c.height, cssW: b.width, cssH: b.height, ratio: devicePixelRatio }; })()`);
    if (Math.abs(size.w - size.cssW * size.ratio) <= 1 && Math.abs(size.h - size.cssH * size.ratio) <= 1)
      pass(`backing store ${size.w}x${size.h} is the canvas's ${size.cssW}x${size.cssH} CSS px at ${size.ratio}`);
    else fail(`backing store ${size.w}x${size.h} for ${size.cssW}x${size.cssH} CSS px at ${size.ratio}`);
    if (first?.ink > 40 && first.levels > 3) pass(`the label is drawn: ${first.ink} pixels of ink in ${first.levels} levels`);
    else fail(`the label is not drawn: ${JSON.stringify(first)}`);

    const mouse = (type, x, y, extra = {}) => send('Input.dispatchMouseEvent', { type, x, y, button: 'left', clickCount: 1, ...extra });
    const idle = await button();
    await mouse('mouseMoved', 40, 33, { button: 'none' });
    await sleep(400);
    const hot = await button();
    if (hot.hash !== idle.hash) pass('the button lights under the pointer'); else fail('the button does not change under the pointer');

    await mouse('mousePressed', 40, 33); await sleep(60); await mouse('mouseReleased', 40, 33); await sleep(300);
    const once = await label();
    for (let k = 0; k < 2; k++) { await mouse('mousePressed', 40, 33); await sleep(60); await mouse('mouseReleased', 40, 33); await sleep(150); }
    await sleep(300);
    const thrice = await label();
    if (once.hash !== first.hash && thrice.hash !== once.hash) pass('each click changes the label');
    else fail(`clicks do not change the label: ${first.hash} ${once.hash} ${thrice.hash}`);

    if (process.env.SMOKE_SCREENSHOTS) {
      await mkdir(process.env.SMOKE_SCREENSHOTS, { recursive: true });
      const shot = await send('Page.captureScreenshot', { format: 'png' });
      await writeFile(join(process.env.SMOKE_SCREENSHOTS, `counter-${ratio}.png`), Buffer.from(shot.result.data, 'base64'));
    }
    if (errors.length === 0) pass('no errors on the console');
    else fail(`errors on the console: ${errors.slice(0, 3).join(' | ')}`);
    ws.close();
  } catch (e) {
    fail(`could not drive the page: ${e}`);
  } finally {
    browser.kill();
    await sleep(200);
    await rm(profile, { recursive: true, force: true }).catch(() => {});
  }
}
server.close();
console.log(failures === 0 ? '\nbrowser smoke test passed' : `\n${failures} failed`);
process.exit(failures === 0 ? 0 : 1);
