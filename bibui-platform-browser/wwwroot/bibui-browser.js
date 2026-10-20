// Bibui.Platform.Browser's half in the page: the canvases bibui draws on,
// their input, and the one requestAnimationFrame loop that draws them.
// BrowserWindow (C#) imports this module; a page that embeds bibui apps may
// import it too, for boot(). It touches nothing but the canvases it is given:
// no global CSS, no elements of its own.
//
// Served beside the app's _framework/ at _content/Bibui.Platform.Browser/.

// ── Booting .NET from a page ─────────────────────────────────────────────────

let booting = null;

/**
 * Loads the app's .NET runtime from the _framework/ next to this module's
 * _content/ folder, and runs its Main: for a page that embeds a bibui app it
 * did not start itself. Returns the runtime's API (getAssemblyExports, ...).
 * The same promise every time. An app whose own main.js starts .NET does not
 * need it.
 */
export function boot() {
  booting ??= (async () => {
    const { dotnet } = await import(new URL('../../_framework/dotnet.js', import.meta.url).href);
    const runtime = await dotnet.create();
    await runtime.runMain();
    return runtime;
  })();
  return booting;
}

// ── Canvases ─────────────────────────────────────────────────────────────────

// Records and their kinds, as BrowserInput.cs reads them.
const STRIDE = 7;
const MOVE = 1, DOWN = 2, UP = 3, WHEEL = 4, KEY_DOWN = 5, KEY_UP = 6, FOCUS_LOST = 7, PASTE = 8;

// Keys whose browser default (scrolling the page, going back) a focused canvas
// does not let through. Tab is not one: bibui does not move focus with it, so
// it leaves the canvas as it would any other element.
const KEPT = new Set(['Space', 'ArrowLeft', 'ArrowRight', 'ArrowUp', 'ArrowDown', 'PageUp', 'PageDown',
  'Home', 'End', 'Backspace', 'Delete', 'Enter', 'NumpadEnter']);

// The modifier bits BrowserKeys.cs reads: Shift 1, Ctrl 2, Alt 4, Meta 8.
const mods = e => (e.shiftKey ? 1 : 0) | (e.ctrlKey ? 2 : 0) | (e.altKey ? 4 : 0) | (e.metaKey ? 8 : 0);

const canvases = new Map();   // handle -> state
let nextHandle = 1;

/** A window on the canvas with this element id: its handle, or 0 when there is no such canvas. */
export function attachById(id) {
  return attach(document.getElementById(id));
}

/** A window on this canvas element: its handle, or 0 when it is not a canvas. */
export function attach(canvas) {
  if (!(canvas instanceof HTMLCanvasElement)) return 0;
  const handle = nextHandle++;
  const s = {
    canvas, ctx: canvas.getContext('2d'), image: null, bytes: null,
    width: 0, height: 0, ratio: window.devicePixelRatio || 1, visible: true,
    records: [], strings: [], polled: [], pasted: null, cleanup: [],
  };
  canvases.set(handle, s);
  const on = (target, type, fn, options) => {
    target.addEventListener(type, fn, options);
    s.cleanup.push(() => target.removeEventListener(type, fn, options));
  };
  const push = (...r) => { while (r.length < STRIDE) r.push(0); s.records.push(...r); wake(); };
  const text = str => { s.strings.push(str); return s.strings.length - 1; };

  keepCssSize(canvas);

  // Size: the canvas's CSS size in device pixels. The backing store is set to
  // it when a frame of that size is presented, so the canvas is never cleared
  // and left blank in between.
  const measured = (w, h) => {
    w = Math.max(1, Math.round(w)); h = Math.max(1, Math.round(h));
    const ratio = window.devicePixelRatio || 1;
    if (w !== s.width || h !== s.height || ratio !== s.ratio) { s.width = w; s.height = h; s.ratio = ratio; wake(); }
  };
  const fromCss = () => { const r = canvas.getBoundingClientRect(), d = window.devicePixelRatio || 1; measured(r.width * d, r.height * d); };
  const resizes = new ResizeObserver(([entry]) => {
    const device = entry.devicePixelContentBoxSize?.[0];
    if (device) measured(device.inlineSize, device.blockSize);
    else { const d = window.devicePixelRatio || 1; measured(entry.contentRect.width * d, entry.contentRect.height * d); }
  });
  try { resizes.observe(canvas, { box: 'device-pixel-content-box' }); } catch { resizes.observe(canvas); }
  s.cleanup.push(() => resizes.disconnect());
  // devicePixelRatio changing (zoom, another monitor) with the CSS size the same.
  let ratioQuery = null;
  const onRatio = () => { fromCss(); watchRatio(); };
  const watchRatio = () => {
    ratioQuery?.removeEventListener('change', onRatio);
    ratioQuery = matchMedia(`(resolution: ${window.devicePixelRatio || 1}dppx)`);
    ratioQuery.addEventListener('change', onRatio);
  };
  watchRatio();
  s.cleanup.push(() => ratioQuery?.removeEventListener('change', onRatio));
  fromCss();

  // In the viewport: a canvas out of it draws nothing. Until the observer
  // first reports, it is what its rectangle says now.
  const r = canvas.getBoundingClientRect();
  s.visible = r.bottom > 0 && r.right > 0 && r.top < innerHeight && r.left < innerWidth;
  const sight = new IntersectionObserver(entries => {
    const visible = entries[entries.length - 1].isIntersecting;
    if (visible !== s.visible) { s.visible = visible; wake(); }
  });
  sight.observe(canvas);
  s.cleanup.push(() => sight.disconnect());

  // Pointer, in CSS pixels from the canvas's top left.
  const at = e => { const r = canvas.getBoundingClientRect(); return [e.clientX - r.left, e.clientY - r.top]; };
  const move = (x, y, m) => {
    // One move a frame is all the runtime keeps: the last one replaces an unread one.
    const n = s.records.length;
    if (n >= STRIDE && s.records[n - STRIDE] === MOVE) { s.records[n - STRIDE + 1] = x; s.records[n - STRIDE + 2] = y; s.records[n - STRIDE + 4] = m; wake(); }
    else push(MOVE, x, y, -1, m);
  };
  on(canvas, 'pointermove', e => move(...at(e), mods(e)));
  on(canvas, 'pointerdown', e => {
    canvas.setPointerCapture?.(e.pointerId);
    canvas.focus({ preventScroll: true });
    push(DOWN, ...at(e), e.button, mods(e));
    e.preventDefault();   // no text selection, no focus moved by the press itself
  });
  on(canvas, 'pointerup', e => push(UP, ...at(e), e.button, mods(e)));
  on(canvas, 'pointercancel', e => push(UP, ...at(e), e.button, mods(e)));
  // Off the canvas, the pointer is over nothing of it, so nothing stays hot.
  on(canvas, 'pointerleave', e => { if (!canvas.hasPointerCapture?.(e.pointerId)) move(-1e4, -1e4, 0); });
  on(canvas, 'contextmenu', e => e.preventDefault());
  // The wheel scrolls the page, unless the canvas has focus.
  on(canvas, 'wheel', e => {
    if (document.activeElement !== canvas) return;
    e.preventDefault();
    push(WHEEL, ...at(e), e.deltaX, e.deltaY, e.deltaMode, mods(e));
  }, { passive: false });

  // Keys while the canvas has focus. A key-down carries its code (the physical
  // key) and its key (what it types); BrowserKeys.cs decides which it is.
  if (!canvas.hasAttribute('tabindex')) canvas.tabIndex = 0;
  on(canvas, 'keydown', e => {
    if (e.isComposing) return;
    const altGraph = !!e.getModifierState?.('AltGraph');
    const shortcut = (e.ctrlKey || e.metaKey) && !altGraph;
    // Ctrl+V and Cmd+V: the browser fires 'paste' with the text, below.
    if (shortcut && e.code === 'KeyV') return;
    push(KEY_DOWN, text(e.code), text(e.key), mods(e), altGraph ? 1 : 0);
    const types = !shortcut && [...e.key].length === 1;
    if (shortcut ? e.code === 'KeyA' : (types || KEPT.has(e.code))) e.preventDefault();
  });
  on(canvas, 'keyup', e => push(KEY_UP, text(e.code), mods(e)));
  on(canvas, 'paste', e => {
    const pasted = e.clipboardData?.getData('text/plain');
    if (pasted) { push(PASTE, text(pasted)); e.preventDefault(); }
  });
  on(canvas, 'blur', () => push(FOCUS_LOST));

  s.detach = () => {
    for (const undo of s.cleanup) undo();
    canvas.style.cursor = '';
  };
  return handle;
}

// A canvas with no CSS size of its own is as large as its backing store: set
// to device pixels at a ratio of 2, it would double, and the next measure
// double it again. Such a canvas keeps the CSS size it had when it was opened.
function keepCssSize(canvas) {
  const { width, height } = canvas;
  const before = canvas.getBoundingClientRect();
  canvas.width = width + 7; canvas.height = height + 7;
  const after = canvas.getBoundingClientRect();
  canvas.width = width; canvas.height = height;
  if (after.width !== before.width) canvas.style.width = `${before.width}px`;
  if (after.height !== before.height) canvas.style.height = `${before.height}px`;
}

/** Lets go of the canvas: its listeners and observers go, and it keeps its last frame. */
export function detach(handle) {
  canvases.get(handle)?.detach();
  canvases.delete(handle);
}

/** The canvas's state and the input since the last poll, as BrowserInput.cs reads them. */
export function poll(handle) {
  const s = canvases.get(handle);
  if (!s) return [0, 1, 1, 1, 0];
  const count = s.records.length / STRIDE;
  const data = [s.visible ? 1 : 0, s.width, s.height, s.ratio, count, ...s.records];
  s.records.length = 0;
  s.polled = s.strings;
  s.strings = [];
  return data;
}

/** The strings the last poll's records index. */
export function strings(handle) {
  return canvases.get(handle)?.polled ?? [];
}

/** Puts a frame (RGBA, straight alpha) on the canvas, setting its backing store to the frame's size. */
export function present(handle, span, width, height) {
  const s = canvases.get(handle);
  if (!s) return;
  if (!s.image || s.image.width !== width || s.image.height !== height) {
    s.image = new ImageData(width, height);
    s.bytes = new Uint8Array(s.image.data.buffer);   // copyTo takes a Uint8Array, not the clamped one
  }
  span.copyTo(s.bytes);   // the one copy out of the WebAssembly heap
  if (s.canvas.width !== width) s.canvas.width = width;
  if (s.canvas.height !== height) s.canvas.height = height;
  s.ctx.putImageData(s.image, 0, 0);
}

/** The pointer's shape over the canvas: a CSS cursor, or '' for the default. */
export function setCursor(handle, css) {
  const s = canvases.get(handle);
  if (s) s.canvas.style.cursor = css;
}

/** Copies text to the system clipboard, when the page may. */
export function writeClipboard(text) {
  navigator.clipboard?.writeText(text).catch(() => {});
}

// ── The loop ─────────────────────────────────────────────────────────────────

let tick = null;        // BrowserWindow's: draws what is owed; true when more is
let scheduled = false;
let woken = false;
let ticking = false;

/** Starts the loop with BrowserWindow's tick. */
export function start(fn) {
  tick = fn;
  wake();
}

/** Asks for a frame: input arrived, a canvas changed, or a runtime asked for frames. */
export function wake() {
  woken = true;
  if (scheduled || ticking || !tick) return;
  scheduled = true;
  requestAnimationFrame(frame);
}

function frame(now) {
  scheduled = false;
  woken = false;
  let more = false;
  ticking = true;
  try { more = tick(now); }
  catch (err) {
    // .NET is gone (it exited, or failed): nothing can draw, so the loop stops.
    console.error('bibui: the frame loop failed, and stops', err);
    tick = null;
    more = woken = false;
  }
  finally { ticking = false; }
  // Woken during the tick (a runtime asked for frames, input arrived): draw again too.
  if (more || woken) { woken = false; scheduled = true; requestAnimationFrame(frame); }
}
