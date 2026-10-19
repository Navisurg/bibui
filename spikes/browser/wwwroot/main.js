// The browser host: one bibui frame per requestAnimationFrame, drawn in C#
// into memory, put on the canvas with putImageData. Pointer events go to C#
// in CSS (logical) pixels; the canvas backing store is CSS size x devicePixelRatio.
import { dotnet } from './_framework/dotnet.js';

const t0 = performance.now();
const canvas = document.getElementById('ui');
const ctx = canvas.getContext('2d');
const stats = document.getElementById('stats');
let image = null, bytes = null;

const { setModuleImports, getAssemblyExports, getConfig, runMain } = await dotnet.create();
setModuleImports('main.js', {
  present(span, w, h) {
    if (!image || image.width !== w || image.height !== h) {
      image = new ImageData(w, h);
      bytes = new Uint8Array(image.data.buffer);   // copyTo takes a Uint8Array, not the clamped one
    }
    span.copyTo(bytes);   // one copy out of the wasm heap
    ctx.putImageData(image, 0, 0);
  },
});
const tReady = performance.now();
const host = (await getAssemblyExports(getConfig().mainAssemblyName)).Host;
await runMain();

const params = new URLSearchParams(location.search);
host.Start(params.has('heavy'));

function resize() {
  const dpr = window.devicePixelRatio || 1;
  const r = canvas.getBoundingClientRect();
  canvas.width = Math.round(r.width * dpr);
  canvas.height = Math.round(r.height * dpr);
  host.Resize(canvas.width, canvas.height, dpr);
}
resize();
new ResizeObserver(resize).observe(canvas);
matchMedia(`(resolution: ${window.devicePixelRatio}dppx)`).addEventListener?.('change', resize);

const pos = e => { const r = canvas.getBoundingClientRect(); return [e.clientX - r.left, e.clientY - r.top]; };
canvas.addEventListener('pointermove', e => host.PointerMove(...pos(e)));
canvas.addEventListener('pointerdown', e => { canvas.setPointerCapture(e.pointerId); host.PointerDown(...pos(e), e.button); });
canvas.addEventListener('pointerup', e => host.PointerUp(...pos(e), e.button));
canvas.addEventListener('wheel', e => { e.preventDefault(); host.Wheel(...pos(e), -e.deltaX / 100, -e.deltaY / 100); }, { passive: false });
canvas.addEventListener('contextmenu', e => e.preventDefault());

// Frame timing: ?bench forces a frame every rAF so steady-state cost is measured.
const bench = params.has('bench');
const samples = [];
let first = null;
function tick() {
  const f0 = performance.now();
  if (host.Frame(bench)) {
    const ms = performance.now() - f0;
    if (first === null) {
      first = performance.now() - t0;
      window.bibuiFirstFrame = { sinceScript: first, runtimeReady: tReady - t0, firstFrameMs: ms };
    }
    samples.push({ total: ms, build: host.BuildMs(), draw: host.DrawMs(), convert: host.ConvertMs() });
    if (samples.length > 120) samples.shift();
    const avg = k => (samples.reduce((a, s) => a + s[k], 0) / samples.length).toFixed(2);
    window.bibuiStats = { n: samples.length, total: +avg('total'), build: +avg('build'), draw: +avg('draw'), convert: +avg('convert'), count: host.Count() };
    stats.textContent = `first frame ${first.toFixed(0)} ms after script | frame ${avg('total')} ms (build ${avg('build')}, draw ${avg('draw')}, convert ${avg('convert')}) avg of ${samples.length} | ${canvas.width}x${canvas.height} @${devicePixelRatio} | count ${host.Count()}`;
  }
  requestAnimationFrame(tick);
}
requestAnimationFrame(tick);
