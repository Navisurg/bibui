using System.Diagnostics;

namespace Bibui.Runtime;

/// <summary>
/// Crash-safe clipboard wrapper. TextCopy 6.x shells out to xsel on Linux and throws
/// when xsel is missing — that exception escapes through GLFW callbacks and aborts the
/// process via PAL_SEHException. This helper catches that, and on Linux also falls
/// back to wl-clipboard (wl-paste / wl-copy), which is standard on Wayland sessions.
/// </summary>
internal static class Clipboard
{
    public static string? GetText()
    {
        // On Wayland skip TextCopy entirely — it shells out to xsel, which can't
        // reach the Wayland clipboard and throws an exception that's swallowed
        // but surfaces as noisy first-chance exceptions in the debugger.
        if (IsWayland()) return TryRun("wl-paste", "--no-newline");

        try
        {
            var t = TextCopy.ClipboardService.GetText();
            if (!string.IsNullOrEmpty(t)) return t;
        }
        catch { /* TextCopy can't reach the platform clipboard; try fallback */ }

        if (OperatingSystem.IsLinux())
            return TryRun("wl-paste", "--no-newline");

        return null;
    }

    public static void SetText(string text)
    {
        if (IsWayland()) { TryPipe("wl-copy", text); return; }

        try
        {
            TextCopy.ClipboardService.SetText(text);
            return;
        }
        catch { /* fall through to platform-specific fallback */ }

        if (OperatingSystem.IsLinux())
            TryPipe("wl-copy", text);
    }

    private static bool IsWayland() =>
        OperatingSystem.IsLinux() &&
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));

    private static string? TryRun(string fileName, string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo(fileName, arguments)
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return null;
            string output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(2000);
            return output;
        }
        catch { return null; }
    }

    private static void TryPipe(string fileName, string text)
    {
        try
        {
            var psi = new ProcessStartInfo(fileName)
            {
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return;
            p.StandardInput.Write(text);
            p.StandardInput.Close();
            p.WaitForExit(2000);
        }
        catch { /* swallow — clipboard is best-effort */ }
    }
}
