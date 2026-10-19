// Starts .NET and runs the app's Main, which opens its window on the canvas
// and returns. runMain, not run: run exits the runtime when Main returns, and
// the window draws after that, on requestAnimationFrame.
import { dotnet } from './_framework/dotnet.js';

const runtime = await dotnet.create();
await runtime.runMain();
