// The docs page's API for bibui's live examples. Served beside _framework/
// (resolved from this module's own URL, wherever it is served from).
//
//   await boot();                       // loads the .NET runtime, once
//   demos();                            // ["avatar", "badge", "button", ...]
//   const d = mount(canvas, "switch");  // null for an unknown id
//   d.dispose();
//
// The page sets the canvas's CSS size; the host owns canvas.width/height.
import * as host from './bibui-host.js';

/** Loads the .NET runtime once (idempotent; returns the same promise). */
export function boot() { return host.boot(); }

/** The ids of the examples, e.g. ["button", "switch", ...]. Empty before boot() resolves. */
export function demos() { return host.ids(); }

/**
 * Starts the example on the canvas: its own runtime, drawn when it is in the
 * viewport and has something to draw. Null for an unknown id or before boot().
 * @returns {{ dispose(): void } | null}
 */
export function mount(canvas, id) {
  try {
    const s = host.mount(canvas, id);
    return s ? { dispose: () => s.dispose() } : null;
  } catch (err) {
    console.error(`bibui-demos: mount(${id}) failed`, err);
    return null;
  }
}
