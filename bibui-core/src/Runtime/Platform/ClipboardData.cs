using System.Text;

namespace Bibui.Core.Runtime.Platform;

/// <summary>
/// What a window host says the clipboard holds, by mime type: the contract
/// between a platform and the runtime for reading anything but text. The
/// host sets it on <see cref="BibuiRuntime.WindowClipboardData"/> when it
/// opens; the app asks <see cref="BibuiRuntime.ClipboardTypes"/> and
/// <see cref="BibuiRuntime.ClipboardRead"/>, which reach it.
///
/// <para>A host reports what its platform offers and nothing more. The Wayland
/// window lists the selection's own types and reads any of them; a window whose
/// platform has only text (GLFW) uses <see cref="TextClipboardData"/>, which
/// offers <c>text/plain;charset=utf-8</c> while there is text.</para>
///
/// <para>Called on the thread that builds frames, as it is asked. A read may
/// wait on the application that copied, so a host bounds it (half a second on
/// Wayland) and answers null rather than blocking for longer.</para>
/// </summary>
public interface IClipboardData
{
    /// <summary>
    /// The mime types the clipboard offers now, best first as its owner listed
    /// them; empty when it holds nothing a host can read.
    /// </summary>
    IReadOnlyList<string> Types();

    /// <summary>
    /// The clipboard's content as <paramref name="mimeType"/>, matched without
    /// regard to case, or null: when <see cref="Types"/> does not list it, or
    /// the owner did not send it in time.
    /// </summary>
    byte[]? Read(string mimeType);
}

/// <summary>
/// The clipboard of a platform that has text only, by mime type: a host whose
/// clipboard is a string (GLFW's) reports it with this. It offers
/// <see cref="Mime"/> while there is text, and reads as the text's UTF-8 bytes.
/// Nothing else: a picture copied in another application is not offered.
/// </summary>
/// <param name="getText">The clipboard's text, or null (or empty) when it holds none.</param>
public sealed class TextClipboardData(Func<string?> getText) : IClipboardData
{
    /// <summary>The one type text is offered as: UTF-8 plain text.</summary>
    public const string Mime = "text/plain;charset=utf-8";

    private readonly Func<string?> _getText = getText ?? throw new ArgumentNullException(nameof(getText));

    /// <inheritdoc/>
    public IReadOnlyList<string> Types() => string.IsNullOrEmpty(_getText()) ? [] : [Mime];

    /// <inheritdoc/>
    public byte[]? Read(string mimeType)
    {
        if (!string.Equals(mimeType, Mime, StringComparison.OrdinalIgnoreCase)) return null;
        return _getText() is { Length: > 0 } text ? Encoding.UTF8.GetBytes(text) : null;
    }
}
