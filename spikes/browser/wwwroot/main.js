// The counter page: one canvas on the shared host (bibui-host.js), with
// timings in the status line. ?heavy builds 96 buttons; ?bench draws every frame.
import { boot, mount, exportsOf } from './bibui-host.js';

const t0 = performance.now();
await boot();
const tReady = performance.now();
const params = new URLSearchParams(location.search);
const canvas = document.getElementById('ui');
const stats = document.getElementById('stats');
const samples = [];
let first = null;
const surface = mount(canvas, params.has('heavy') ? 'heavy' : 'counter', {
  force: params.has('bench'),
  onFrame(ms) {
    const host = exportsOf(), h = surface.handle;
    if (first === null) {
      first = performance.now() - t0;
      window.bibuiFirstFrame = { sinceScript: first, runtimeReady: tReady - t0, firstFrameMs: ms };
    }
    samples.push({ total: ms, build: host.BuildMs(h), draw: host.DrawMs(h), convert: host.ConvertMs(h) });
    if (samples.length > 120) samples.shift();
    const avg = k => (samples.reduce((a, s) => a + s[k], 0) / samples.length).toFixed(2);
    window.bibuiStats = { n: samples.length, total: +avg('total'), build: +avg('build'), draw: +avg('draw'), convert: +avg('convert') };
    stats.textContent = `first frame ${first.toFixed(0)} ms after script | frame ${avg('total')} ms (build ${avg('build')}, draw ${avg('draw')}, convert ${avg('convert')}) avg of ${samples.length} | ${canvas.width}x${canvas.height} @${devicePixelRatio}`;
  },
});
