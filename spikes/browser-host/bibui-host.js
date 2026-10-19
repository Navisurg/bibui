// The browser host's JS half (BrowserHost.cs is the other): loads the .NET
// runtime from ./_framework next to this module, and drives any number of
// canvases, each with its own bibui runtime, from one requestAnimationFrame
// loop. A canvas is drawn only when it is in the viewport and its runtime
// owes a frame (input, a resize, an animation).
//
// The canvas's CSS size is the page's; the host owns canvas.width/height
// (CSS size x devicePixelRatio). Input goes to C# in CSS pixels.

const FRAMEWORK = new URL('./_framework/dotnet.js', import.meta.url).href;

// KeyboardEvent.code -> bibui's Key (GLFW key codes: physical keys named for
// their place on a US layout, as GLFW, and so the desktop hosts, report them).
const KEYS = (() => {
  const k = {
    Space: 32, Quote: 39, Comma: 44, Minus: 45, Period: 46, Slash: 47, Semicolon: 59, Equal: 61,
    BracketLeft: 91, Backslash: 92, BracketRight: 93, Backquote: 96, IntlBackslash: 161,
    Escape: 256, Enter: 257, Tab: 258, Backspace: 259, Insert: 260, Delete: 261,
    ArrowRight: 262, ArrowLeft: 263, ArrowDown: 264, ArrowUp: 265, PageUp: 266, PageDown: 267, Home: 268, End: 269,
    CapsLock: 280, ScrollLock: 281, NumLock: 282, PrintScreen: 283, Pause: 284,
    NumpadDecimal: 330, NumpadDivide: 331, NumpadMultiply: 332, NumpadSubtract: 333, NumpadAdd: 334,
    NumpadEnter: 335, NumpadEqual: 336,
    ShiftLeft: 340, ControlLeft: 341, AltLeft: 342, MetaLeft: 343,
    ShiftRight: 344, ControlRight: 345, AltRight: 346, MetaRight: 347, ContextMenu: 348,
  };
  for (let i = 0; i < 26; i++) k['Key' + String.fromCharCode(65 + i)] = 65 + i;
  for (let i = 0; i < 10; i++) { k['Digit' + i] = 48 + i; k['Numpad' + i] = 320 + i; }
  for (let i = 1; i <= 24; i++) k['F' + i] = 289 + i;
  return k;
})();

// Keys whose browser default (scrolling the page, going back) a focused
// canvas should not get. Tab is not among them: bibui does not move focus
// with it, so it leaves the canvas as it would any other element.
const NAV = new Set(['Space', 'ArrowLeft', 'ArrowRight', 'ArrowUp', 'ArrowDown', 'PageUp', 'PageDown',
  'Home', 'End', 'Backspace', 'Delete', 'Enter', 'NumpadEnter']);

// KeyModifiers: Shift 1, Ctrl 2, Alt 4, Super 8 (Cmd; the runtime folds it into Ctrl).
const mods = e => (e.shiftKey ? 1 : 0) | (e.ctrlKey ? 2 : 0) | (e.altKey ? 4 : 0) | (e.metaKey ? 8 : 0);

let booting = null;
let host = null;              // BrowserHost's exports
const surfaces = new Map();   // handle -> surface
let looping = false;

/** Loads the .NET runtime once; the same promise every time. */
export function boot() {
  booting ??= (async () => {
    const { dotnet } = await import(FRAMEWORK);
    const runtime = await dotnet.create();
    runtime.setModuleImports('bibui-host', {
      present(handle, span, w, h) {
        const s = surfaces.get(handle);
        if (!s) return;
        if (!s.image || s.image.width !== w || s.image.height !== h) {
          s.image = new ImageData(w, h);
          s.bytes = new Uint8Array(s.image.data.buffer);   // copyTo takes a Uint8Array, not the clamped one
        }
        span.copyTo(s.bytes);   // one copy out of the wasm heap
        if (s.canvas.width === w && s.canvas.height === h) s.ctx.putImageData(s.image, 0, 0);
      },
      writeClipboard(text) { navigator.clipboard?.writeText(text).catch(() => {}); },
    });
    const exports = await runtime.getAssemblyExports(runtime.getConfig().mainAssemblyName);
    host = exports.Bibui.Browser.BrowserHost;
    await runtime.runMain();   // the app's Main sets BrowserHost.Factory
  })();
  return booting;
}

/** The ids the app can build. boot() first. */
export function ids() { return host ? host.List() : []; }

/** Exports for stats and tests. boot() first. */
export function exportsOf() { return host; }

/**
 * Starts the surface named by id on the canvas. Null for an unknown id (or
 * before boot). opts.force draws every frame (benchmarks).
 */
export function mount(canvas, id, opts = {}) {
  if (!host) return null;
  const handle = host.Create(id);
  if (!handle) return null;
  const s = { handle, canvas, ctx: canvas.getContext('2d'), image: null, bytes: null,
              visible: true, force: !!opts.force, onFrame: opts.onFrame, cleanup: [] };
  surfaces.set(handle, s);
  const on = (target, type, fn, o) => { target.addEventListener(type, fn, o); s.cleanup.push(() => target.removeEventListener(type, fn, o)); };

  // Size: CSS size x devicePixelRatio, exact where the browser reports device pixels.
  const resize = (w, h) => {
    w = Math.max(1, Math.round(w)); h = Math.max(1, Math.round(h));
    if (canvas.width !== w) canvas.width = w;
    if (canvas.height !== h) canvas.height = h;
    host.Resize(handle, w, h, window.devicePixelRatio || 1);
  };
  const ro = new ResizeObserver(([entry]) => {
    const dpr = window.devicePixelRatio || 1;
    const dev = entry.devicePixelContentBoxSize?.[0];
    if (dev) resize(dev.inlineSize, dev.blockSize);
    else resize(entry.contentRect.width * dpr, entry.contentRect.height * dpr);
  });
  try { ro.observe(canvas, { box: 'device-pixel-content-box' }); } catch { ro.observe(canvas); }
  s.cleanup.push(() => ro.disconnect());
  // A DPR change (zoom, another monitor) that leaves the CSS size alone.
  let mq;
  const watchDpr = () => {
    mq?.removeEventListener('change', onDpr);
    mq = matchMedia(`(resolution: ${window.devicePixelRatio}dppx)`);
    mq.addEventListener('change', onDpr);
  };
  const onDpr = () => { const r = canvas.getBoundingClientRect(); resize(r.width * devicePixelRatio, r.height * devicePixelRatio); watchDpr(); };
  watchDpr();
  s.cleanup.push(() => mq?.removeEventListener('change', onDpr));
  { const r = canvas.getBoundingClientRect(); resize(r.width * devicePixelRatio, r.height * devicePixelRatio); }

  // Drawn only in the viewport.
  const io = new IntersectionObserver(([entry]) => { s.visible = entry.isIntersecting; });
  io.observe(canvas);
  s.cleanup.push(() => io.disconnect());

  // Pointer, in CSS pixels.
  const pos = e => { const r = canvas.getBoundingClientRect(); return [e.clientX - r.left, e.clientY - r.top]; };
  on(canvas, 'pointermove', e => host.Pointer(handle, 0, ...pos(e), -1, mods(e)));
  on(canvas, 'pointerdown', e => {
    canvas.setPointerCapture?.(e.pointerId);
    canvas.focus({ preventScroll: true });
    host.Pointer(handle, 1, ...pos(e), e.button, mods(e));
    e.preventDefault();   // no text selection or focus change by the press itself
  });
  on(canvas, 'pointerup', e => host.Pointer(handle, 2, ...pos(e), e.button, mods(e)));
  on(canvas, 'pointercancel', e => host.Pointer(handle, 2, ...pos(e), e.button, mods(e)));
  // Out of the canvas: the pointer is nowhere over it, so nothing stays hot.
  on(canvas, 'pointerleave', e => { if (!canvas.hasPointerCapture?.(e.pointerId)) host.Pointer(handle, 0, -1e4, -1e4, -1, 0); });
  on(canvas, 'contextmenu', e => e.preventDefault());
  // The wheel scrolls the page unless the canvas has focus (it was clicked).
  on(canvas, 'wheel', e => {
    if (document.activeElement !== canvas) return;
    e.preventDefault();
    const unit = e.deltaMode === 1 ? 1 / 3 : e.deltaMode === 2 ? 3 : 1 / 100;   // lines, pages, pixels -> notches
    host.Wheel(handle, ...pos(e), -e.deltaX * unit, -e.deltaY * unit, mods(e));
  }, { passive: false });

  // Keyboard: the canvas takes focus on a press, and keys while it has it.
  if (!canvas.hasAttribute('tabindex')) canvas.tabIndex = 0;
  on(canvas, 'keydown', e => {
    if (e.isComposing) return;
    const altGr = e.getModifierState?.('AltGraph');
    const shortcut = (e.ctrlKey || e.metaKey) && !altGr;
    // Paste: let the browser fire 'paste', which carries the text (below).
    if (shortcut && e.code === 'KeyV') return;
    const key = KEYS[e.code] ?? -1;
    const printable = !shortcut && [...e.key].length === 1;
    host.KeyEvent(handle, true, key, altGr ? mods(e) & ~6 : mods(e), printable ? e.key : null);
    if (shortcut ? e.code === 'KeyA' : (printable || NAV.has(e.code))) e.preventDefault();
  });
  on(canvas, 'keyup', e => { const key = KEYS[e.code]; if (key !== undefined) host.KeyEvent(handle, false, key, mods(e), null); });
  on(canvas, 'paste', e => {
    const text = e.clipboardData?.getData('text/plain');
    if (text) { host.Paste(handle, text); e.preventDefault(); }
  });
  on(canvas, 'blur', () => host.Blur(handle));

  startLoop();
  return {
    handle,
    dispose() {
      if (!surfaces.delete(handle)) return;
      for (const f of s.cleanup) f();
      host.Destroy(handle);
    },
  };
}

function startLoop() {
  if (looping) return;
  looping = true;
  const tick = () => {
    if (surfaces.size === 0) { looping = false; return; }
    for (const s of surfaces.values()) {
      if (!s.visible || s.failed) continue;
      const t0 = performance.now();
      let drew = false;
      try { drew = host.Frame(s.handle, s.force); }
      catch (err) { s.failed = true; console.error('bibui frame failed; this surface stops', err); }
      if (drew && s.onFrame) s.onFrame(performance.now() - t0);
    }
    requestAnimationFrame(tick);
  };
  requestAnimationFrame(tick);
}
