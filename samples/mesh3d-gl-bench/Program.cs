// Mesh3D's frame time on the GL renderer (SkiaGlRenderer), with no window on
// screen: a GLFW window that is never made visible gives the GL context, and
// the frames are drawn into a framebuffer of its own, read back once as a PNG
// to show what was drawn. A grid of 317 × 317 vertices (100,489, 199,712
// triangles) ripples: its positions are replaced every frame.
//
//   dotnet run --project samples/mesh3d-gl-bench -c Release -- [frames] [out.png]
using System.Diagnostics;
using System.Numerics;
using Bibui.Core;
using Bibui.Core.Imaging;
using Bibui.Core.Runtime;
using Bibui.Core.Runtime.Platform;
using Bibui.Gl;
using Bibui.Raster.Skia;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;

const int Width = 1280, Height = 800, N = 317;
int frames = args.Length > 0 ? int.Parse(args[0]) : 120;
string? png = args.Length > 1 ? args[1] : null;

SilkWindow.LoadBundledGlfw();
Silk.NET.Windowing.Glfw.GlfwWindowing.RegisterPlatform();
var options = SilkGl.Options(new PlatformWindowOptions("mesh3d-gl-bench", 64, 64)) with { IsVisible = false };
using var window = Window.Create(options);
window.Initialize();
var context = window.GLContext!;
context.MakeCurrent();
var gl = GL.GetApi(name => context.TryGetProcAddress(name, out var at) ? at : 0);
Console.WriteLine($"GL: {gl.GetStringS(StringName.Renderer)} — {gl.GetStringS(StringName.Version)}");

// A framebuffer of its own, colour and stencil, at the size measured.
uint fbo = gl.GenFramebuffer();
uint colour = gl.GenRenderbuffer(), stencil = gl.GenRenderbuffer();
gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, colour);
gl.RenderbufferStorage(RenderbufferTarget.Renderbuffer, InternalFormat.Rgba8, Width, Height);
gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, stencil);
gl.RenderbufferStorage(RenderbufferTarget.Renderbuffer, InternalFormat.Depth24Stencil8, Width, Height);
gl.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, RenderbufferTarget.Renderbuffer, colour);
gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthStencilAttachment, RenderbufferTarget.Renderbuffer, stencil);
var status = gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
if (status != GLEnum.FramebufferComplete) throw new Exception($"framebuffer: {status}");

var target = new GlTarget
{
    GetProcAddress = name => context.TryGetProcAddress(name, out var at) ? at : 0,
    Framebuffer = fbo, StencilBits = 8, Width = Width, Height = Height,
};
using var renderer = new SkiaGlRenderer(target);

// The mesh: a grid over [-1, 1]² rippling in y.
var faces = new uint[(N - 1) * (N - 1) * 6];
for (int i = 0, k = 0; i < N - 1; i++)
    for (int j = 0; j < N - 1; j++, k += 6)
    {
        uint a = (uint)(i * N + j), b = a + 1, c = a + N, d = c + 1;
        (faces[k], faces[k + 1], faces[k + 2], faces[k + 3], faces[k + 4], faces[k + 5]) = (a, c, b, b, c, d);
    }
var xyz = new float[N * N * 3];
var mesh = new TriangleMesh(faces, N * N);
var heights = new float[N * N];
var meshOptions = new Mesh3DOptions { Home = new OrbitCamera(new Vector3(0f, 1.4f, 2.2f), Vector3.Zero, Vector3.UnitY) };

var runtime = BibuiRuntime.Create();
var window1 = new WindowFrame(Width, Height, 1f, 60f);
var build = new Stopwatch();
var draw = new Stopwatch();
for (int f = -10; f < frames; f++)   // ten frames to warm up, not counted
{
    if (f == 0) { build.Reset(); draw.Reset(); }
    build.Start();
    float t = f * 0.05f;
    for (int i = 0; i < N; i++)
        for (int j = 0; j < N; j++)
        {
            float x = -1f + 2f * j / (N - 1), z = -1f + 2f * i / (N - 1), r = MathF.Sqrt(x * x + z * z);
            int v = 3 * (i * N + j);
            (xyz[v], xyz[v + 1], xyz[v + 2]) = (x, 0.12f * MathF.Sin(9f * r - 3f * t) * MathF.Exp(-r), z);
            heights[i * N + j] = xyz[v + 1];
        }
    mesh.SetPositions(xyz);
    mesh.SetScalars(heights, -0.12f, 0.12f);
    runtime.BeginBuild(window1);
    runtime.Mesh3D("bench", mesh, meshOptions);
    runtime.EndBuild();   // lays out and paints: the mesh is rasterised here
    build.Stop();
    draw.Start();
    renderer.Draw(runtime.DrawList);
    gl.Finish();          // the GPU done with the frame, upload included
    draw.Stop();
}
double ms(Stopwatch s) => s.Elapsed.TotalMilliseconds / frames;
Console.WriteLine($"{N * N:N0} vertices, {faces.Length / 3:N0} triangles at {Width}×{Height}, {frames} frames, {Environment.ProcessorCount} cores:");
Console.WriteLine($"  positions + build + raster {ms(build):F1} ms, GL draw + finish {ms(draw):F1} ms, total {ms(build) + ms(draw):F1} ms a frame ({1000.0 / (ms(build) + ms(draw)):F0} fps)");

if (png is not null)
{
    var pixels = new byte[Width * Height * 4];
    gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, fbo);
    unsafe { fixed (byte* p = pixels) gl.ReadPixels(0, 0, Width, Height, PixelFormat.Rgba, PixelType.UnsignedByte, p); }
    // GL's rows run bottom-up.
    var flipped = new byte[pixels.Length];
    for (int y = 0; y < Height; y++) Array.Copy(pixels, (Height - 1 - y) * Width * 4, flipped, y * Width * 4, Width * 4);
    File.WriteAllBytes(png, Png.Encode(flipped, Width, Height, Width * 4, ChannelOrder.Rgba, AlphaMode.Premultiplied));
    Console.WriteLine($"  the last frame: {png}");
}
