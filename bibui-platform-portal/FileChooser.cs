using System.Security.Cryptography;
using Bibui.Platform.Portal.DBus;

namespace Bibui.Platform.Portal;

/// <summary>
/// A filter the file chooser offers: a name the person reads, and the files it
/// shows, by glob (<c>*.png</c>, matched without regard to case by most
/// portals) or by MIME type (<c>image/*</c>).
/// </summary>
public sealed class FileFilter
{
    /// <summary>A filter named <paramref name="name"/> showing the files that match any of <paramref name="globs"/>.</summary>
    public FileFilter(string name, params string[] globs)
    {
        Name = name;
        Globs = globs;
    }

    /// <summary>What the chooser calls the filter, as "Images".</summary>
    public string Name { get; }

    /// <summary>Shell-style patterns: <c>*.png</c>, <c>README*</c>.</summary>
    public IReadOnlyList<string> Globs { get; }

    /// <summary>MIME types, wildcards allowed: <c>image/*</c>, <c>text/markdown</c>.</summary>
    public IReadOnlyList<string> MimeTypes { get; init; } = [];
}

/// <summary>What the file chooser asks of the person.</summary>
public sealed class FileChooserOptions
{
    /// <summary>The dialog's title, as "Attach files".</summary>
    public string Title { get; init; } = "";

    /// <summary>The label of the button that accepts, as "Attach". Empty: the portal's own ("Open").</summary>
    public string AcceptLabel { get; init; } = "";

    /// <summary>Whether more than one file (or folder) may be chosen.</summary>
    public bool Multiple { get; init; }

    /// <summary>
    /// Whether folders are chosen instead of files. Needs version 3 of the
    /// portal's FileChooser (<see cref="FileChooser.VersionAsync"/>); an older
    /// one ignores it and chooses files.
    /// </summary>
    public bool Directory { get; init; }

    /// <summary>Whether the dialog is modal to <see cref="ParentWindow"/>. True unless set.</summary>
    public bool Modal { get; init; } = true;

    /// <summary>The filters offered, in order; empty offers every file.</summary>
    public IReadOnlyList<FileFilter> Filters { get; init; } = [];

    /// <summary>The filter selected when the dialog opens: one of <see cref="Filters"/>, or null for the portal's choice.</summary>
    public FileFilter? CurrentFilter { get; init; }

    /// <summary>
    /// The folder the dialog opens in, a local path. Needs version 4 of the
    /// portal's FileChooser; an older one ignores it. Null: the portal's choice.
    /// </summary>
    public string? CurrentFolder { get; init; }

    /// <summary>
    /// The window the dialog belongs to, as the portal names one:
    /// <c>wayland:HANDLE</c> (from xdg-foreign) or <c>x11:XID</c> in hex. Empty,
    /// the default, opens the dialog on its own, unparented.
    /// </summary>
    public string ParentWindow { get; init; } = "";
}

/// <summary>What the save dialog asks of the person.</summary>
public sealed class SaveFileOptions
{
    /// <summary>The dialog's title, as "Save the report".</summary>
    public string Title { get; init; } = "";

    /// <summary>The label of the button that accepts, as "Export". Empty: the portal's own ("Save").</summary>
    public string AcceptLabel { get; init; } = "";

    /// <summary>Whether the dialog is modal to <see cref="ParentWindow"/>. True unless set.</summary>
    public bool Modal { get; init; } = true;

    /// <summary>The filters offered, in order; empty offers every file.</summary>
    public IReadOnlyList<FileFilter> Filters { get; init; } = [];

    /// <summary>The filter selected when the dialog opens: one of <see cref="Filters"/>, or null for the portal's choice.</summary>
    public FileFilter? CurrentFilter { get; init; }

    /// <summary>The name the dialog suggests, as "report.md". Null: none.</summary>
    public string? CurrentName { get; init; }

    /// <summary>The folder the dialog opens in, a local path. Null: the portal's choice.</summary>
    public string? CurrentFolder { get; init; }

    /// <summary>
    /// The file being saved again, a local path, for "Save as…" on a file that
    /// has one: the dialog opens beside it, with its name. Null: a new file.
    /// </summary>
    public string? CurrentFile { get; init; }

    /// <summary>
    /// The window the dialog belongs to, as the portal names one:
    /// <c>wayland:HANDLE</c> (from xdg-foreign) or <c>x11:XID</c> in hex. Empty,
    /// the default, opens the dialog on its own, unparented.
    /// </summary>
    public string ParentWindow { get; init; } = "";
}

/// <summary>How a file chooser request ended.</summary>
public enum FileChooserOutcome
{
    /// <summary>The person chose; <see cref="FileChooserResult.Paths"/> holds what.</summary>
    Chosen,

    /// <summary>The person closed the dialog without choosing.</summary>
    Cancelled,

    /// <summary>No file chooser could be asked: no session bus, no portal on it, or a portal without a FileChooser.</summary>
    Unavailable,

    /// <summary>The portal was asked and the request failed: it ended it, refused it, or the bus went away.</summary>
    Failed,
}

/// <summary>What a file chooser request came back with.</summary>
public sealed class FileChooserResult
{
    internal FileChooserResult(FileChooserOutcome outcome, IReadOnlyList<string> paths, string? reason)
    {
        Outcome = outcome;
        Paths = paths;
        Reason = reason;
    }

    /// <summary>How the request ended.</summary>
    public FileChooserOutcome Outcome { get; }

    /// <summary>The chosen files or folders as local paths, in the order the portal gave them (one for a save); empty unless <see cref="Outcome"/> is <see cref="FileChooserOutcome.Chosen"/>.</summary>
    public IReadOnlyList<string> Paths { get; }

    /// <summary>Why nothing was chosen, or what of the portal's answer was left out, in words for a log; null when there is nothing to say.</summary>
    public string? Reason { get; }

    internal static FileChooserResult Unavailable(string reason) => new(FileChooserOutcome.Unavailable, [], reason);
    internal static FileChooserResult Failed(string reason) => new(FileChooserOutcome.Failed, [], reason);
}

/// <summary>
/// The desktop's own file chooser, through xdg-desktop-portal's
/// <c>org.freedesktop.portal.FileChooser</c> on the session bus: the open and
/// save dialogs GNOME, KDE or the compositor's portal draws, in the person's
/// language and with their bookmarks, for any bibui window.
///
/// <para>Every call is asynchronous and holds no thread while the dialog is
/// open, so the frame loop keeps running under it. The task completes on a
/// thread-pool thread: call <c>runtime.RequestFrame()</c> from the
/// continuation (it is safe from any thread), or look at the task in the next
/// build. Without a portal it comes back <see cref="FileChooserOutcome.Unavailable"/>
/// with the reason; it does not throw for that.</para>
/// </summary>
public static class FileChooser
{
    private const string Desktop = "org.freedesktop.portal.Desktop";
    private const string DesktopPath = "/org/freedesktop/portal/desktop";
    private const string ChooserInterface = "org.freedesktop.portal.FileChooser";
    private const string RequestInterface = "org.freedesktop.portal.Request";

    /// <summary>How long connecting to the bus may take.</summary>
    internal static TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long the portal may take to answer a call (not to be answered by
    /// the person): long enough for the bus to start it.
    /// </summary>
    internal static TimeSpan CallTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Asks the person to choose files, or folders, and returns their local
    /// paths; null when they cancelled or no chooser could be asked.
    /// <see cref="ChooseAsync(FileChooserOptions, CancellationToken)"/> says which, and why.
    /// </summary>
    /// <exception cref="ArgumentException">An option holds a NUL character, which D-Bus cannot carry.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled; the dialog is closed.</exception>
    public static Task<IReadOnlyList<string>?> OpenAsync(FileChooserOptions options, CancellationToken ct = default) =>
        OpenAsync(options, OperatingSystem.IsLinux() ? SessionBus.Address() : null, ct);

    internal static async Task<IReadOnlyList<string>?> OpenAsync(FileChooserOptions options, string? address, CancellationToken ct)
    {
        var result = await ChooseAsync(options, address, ct).ConfigureAwait(false);
        return result.Outcome == FileChooserOutcome.Chosen ? result.Paths : null;
    }

    /// <summary>
    /// Asks the person to choose files, or folders, and says how it ended:
    /// the local paths chosen, a cancel, or why no chooser could be asked.
    /// Cancelling <paramref name="ct"/> closes the dialog.
    /// </summary>
    /// <exception cref="ArgumentException">An option holds a NUL character, which D-Bus cannot carry.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled; the dialog is closed.</exception>
    public static Task<FileChooserResult> ChooseAsync(FileChooserOptions options, CancellationToken ct = default) =>
        ChooseAsync(options, OperatingSystem.IsLinux() ? SessionBus.Address() : null, ct);

    /// <summary>
    /// Asks the person where to save a file and returns its local path; null
    /// when they cancelled or no chooser could be asked.
    /// <see cref="ChooseSaveAsync(SaveFileOptions, CancellationToken)"/> says which, and why.
    /// The file is not written: the app writes it, and the dialog has already
    /// asked whether to replace a file of that name.
    /// </summary>
    /// <exception cref="ArgumentException">An option holds a NUL character, which D-Bus cannot carry.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled; the dialog is closed.</exception>
    public static Task<string?> SaveAsync(SaveFileOptions options, CancellationToken ct = default) =>
        SaveAsync(options, OperatingSystem.IsLinux() ? SessionBus.Address() : null, ct);

    internal static async Task<string?> SaveAsync(SaveFileOptions options, string? address, CancellationToken ct)
    {
        var result = await ChooseSaveAsync(options, address, ct).ConfigureAwait(false);
        return result.Outcome == FileChooserOutcome.Chosen ? result.Paths[0] : null;
    }

    /// <summary>
    /// Asks the person where to save a file, and says how it ended: the local
    /// path chosen, a cancel, or why no chooser could be asked. Cancelling
    /// <paramref name="ct"/> closes the dialog.
    /// </summary>
    /// <exception cref="ArgumentException">An option holds a NUL character, which D-Bus cannot carry.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled; the dialog is closed.</exception>
    public static Task<FileChooserResult> ChooseSaveAsync(SaveFileOptions options, CancellationToken ct = default) =>
        ChooseSaveAsync(options, OperatingSystem.IsLinux() ? SessionBus.Address() : null, ct);

    /// <summary>
    /// The version of the portal's FileChooser on the session bus, or null
    /// when there is none (or no bus). Opens no dialog; the bus starts the
    /// portal if it is not running.
    /// </summary>
    public static Task<uint?> VersionAsync(CancellationToken ct = default) =>
        VersionAsync(OperatingSystem.IsLinux() ? SessionBus.Address() : null, ct);

    internal static async Task<uint?> VersionAsync(string? address, CancellationToken ct)
    {
        if (address is null) return null;
        try
        {
            await using var bus = await ConnectAsync(address, ct).ConfigureAwait(false);
            var body = new DBusWriter();
            body.String(ChooserInterface);
            body.String("version");
            var reply = await CallAsync(bus, DBusMessage.Call(Desktop, DesktopPath, "org.freedesktop.DBus.Properties", "Get", "ss", body), ct).ConfigureAwait(false);
            if (reply.Type == DBusMessageType.Error || reply.Signature != "v") return null;
            return ((DBusVariant)reply.ReadBody()[0]).Value as uint?;
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    internal static Task<FileChooserResult> ChooseAsync(FileChooserOptions options, string? address, CancellationToken ct)
    {
        var token = NewToken();
        return Ask("OpenFile", EncodeOpenFile(options, token), token, address, ct);
    }

    internal static Task<FileChooserResult> ChooseSaveAsync(SaveFileOptions options, string? address, CancellationToken ct)
    {
        var token = NewToken();
        return Ask("SaveFile", EncodeSaveFile(options, token), token, address, ct);
    }

    private static string NewToken() => "bibui_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();

    // The body is encoded before this is called, so a bad option throws to the caller.
    private static Task<FileChooserResult> Ask(string method, DBusWriter body, string token, string? address, CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux()) return Task.FromResult(FileChooserResult.Unavailable("xdg-desktop-portal is only on Linux"));
        if (address is null)
            return Task.FromResult(FileChooserResult.Unavailable("no session bus: DBUS_SESSION_BUS_ADDRESS is not set and $XDG_RUNTIME_DIR/bus does not exist"));
        return RequestAsync(method, body, token, address, ct);
    }

    private static async Task<FileChooserResult> RequestAsync(string method, DBusWriter request, string token, string address, CancellationToken ct)
    {
        DBusConnection bus;
        try { bus = await ConnectAsync(address, ct).ConfigureAwait(false); }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return FileChooserResult.Unavailable($"cannot reach the session bus at {address}: {e.Message}");
        }

        await using (bus.ConfigureAwait(false))
        {
            // Every Response that arrives is kept: the portal's reply to
            // the call and its Response can reach the read loop back to back,
            // before this method has looked at the reply.
            var responses = new List<DBusMessage>();
            var arrived = new SemaphoreSlim(0);
            bus.Signal = m =>
            {
                if (m.Interface != RequestInterface || m.Member != "Response") return;
                lock (responses) responses.Add(m);
                arrived.Release();
            };

            string expected = RequestPath(bus.UniqueName, token);
            string handle;
            string? portal;
            try
            {
                // Subscribed before the call, so a Response the portal sends
                // at once cannot be missed.
                await AddMatchAsync(bus, expected, ct).ConfigureAwait(false);
                var reply = await CallAsync(bus, DBusMessage.Call(Desktop, DesktopPath, ChooserInterface, method, "ssa{sv}", request), ct).ConfigureAwait(false);
                if (reply.Type == DBusMessageType.Error)
                {
                    var why = $"{reply.ErrorName}: {reply.ErrorText()}";
                    return IsAbsent(reply.ErrorName)
                        ? FileChooserResult.Unavailable($"no file chooser portal on the session bus ({why})")
                        : FileChooserResult.Failed($"the portal refused the request ({why})");
                }
                if (reply.Signature != "o") return FileChooserResult.Failed($"the portal answered {method} with '{reply.Signature}', not a request handle");
                handle = (string)reply.ReadBody()[0];
                portal = reply.Sender;
                // A portal older than 0.9 names its own handle, unsubscribed.
                if (handle != expected) await AddMatchAsync(bus, handle, ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                return e is OperationCanceledException
                    ? FileChooserResult.Unavailable("the portal did not answer in time")
                    : FileChooserResult.Failed($"the request to the portal failed: {e.Message}");
            }

            while (true)
            {
                DBusMessage? response;
                lock (responses)
                    response = responses.Find(m => m.Path == handle && (portal is null || m.Sender == portal));
                if (response is not null) return ReadResponse(response);

                var next = arrived.WaitAsync(ct);
                if (await Task.WhenAny(next, bus.Closed).ConfigureAwait(false) == bus.Closed)
                    return FileChooserResult.Failed($"the session bus connection closed while the dialog was open: {bus.Closed.Result.Message}");
                try { await next.ConfigureAwait(false); }
                catch (OperationCanceledException)
                {
                    await CloseRequestAsync(bus, handle).ConfigureAwait(false);
                    throw;
                }
            }
        }
    }

    /// <summary>The handle the portal gives a request this connection makes with <paramref name="token"/>.</summary>
    internal static string RequestPath(string uniqueName, string token) =>
        "/org/freedesktop/portal/desktop/request/" + uniqueName.TrimStart(':').Replace('.', '_') + "/" + token;

    /// <summary>The body of <c>OpenFile(parent_window, title, options)</c>, signature <c>ssa{sv}</c>.</summary>
    internal static DBusWriter EncodeOpenFile(FileChooserOptions options, string token)
    {
        var w = new DBusWriter();
        w.String(options.ParentWindow);
        w.String(options.Title);
        var dict = w.BeginArray(8);
        Entry(w, "handle_token", "s").String(token);
        if (options.AcceptLabel.Length > 0) Entry(w, "accept_label", "s").String(options.AcceptLabel);
        Entry(w, "modal", "b").Bool(options.Modal);
        Entry(w, "multiple", "b").Bool(options.Multiple);
        Entry(w, "directory", "b").Bool(options.Directory);
        Filters(w, options.Filters, options.CurrentFilter);
        if (options.CurrentFolder is { } folder) PathEntry(w, "current_folder", folder);
        w.EndArray(dict);
        return w;
    }

    /// <summary>The body of <c>SaveFile(parent_window, title, options)</c>, signature <c>ssa{sv}</c>.</summary>
    internal static DBusWriter EncodeSaveFile(SaveFileOptions options, string token)
    {
        var w = new DBusWriter();
        w.String(options.ParentWindow);
        w.String(options.Title);
        var dict = w.BeginArray(8);
        Entry(w, "handle_token", "s").String(token);
        if (options.AcceptLabel.Length > 0) Entry(w, "accept_label", "s").String(options.AcceptLabel);
        Entry(w, "modal", "b").Bool(options.Modal);
        Filters(w, options.Filters, options.CurrentFilter);
        if (options.CurrentName is { } name) Entry(w, "current_name", "s").String(name);
        if (options.CurrentFolder is { } folder) PathEntry(w, "current_folder", folder);
        if (options.CurrentFile is { } file) PathEntry(w, "current_file", file);
        w.EndArray(dict);
        return w;
    }

    private static void Filters(DBusWriter w, IReadOnlyList<FileFilter> filters, FileFilter? current)
    {
        if (filters.Count > 0)
        {
            Entry(w, "filters", "a(sa(us))");
            var array = w.BeginArray(8);
            foreach (var f in filters) Filter(w, f);
            w.EndArray(array);
        }
        if (current is not null) Filter(Entry(w, "current_filter", "(sa(us))"), current);
    }

    // A path as a byte string with its NUL, as the portal takes a file name.
    private static void PathEntry(DBusWriter w, string key, string path)
    {
        if (path.Contains('\0')) throw new ArgumentException($"{key} cannot hold a NUL character", "options");
        var bytes = new byte[System.Text.Encoding.UTF8.GetByteCount(path) + 1];
        System.Text.Encoding.UTF8.GetBytes(path, bytes);
        Entry(w, key, "ay").Bytes(bytes);
    }

    private static DBusWriter Entry(DBusWriter w, string key, string signature)
    {
        w.BeginStruct();
        w.String(key);
        w.Variant(signature);
        return w;
    }

    // (sa(us)): the name, then (0, glob) and (1, MIME type) pairs.
    private static void Filter(DBusWriter w, FileFilter f)
    {
        w.BeginStruct();
        w.String(f.Name);
        var patterns = w.BeginArray(8);
        foreach (var glob in f.Globs) { w.BeginStruct(); w.UInt32(0); w.String(glob); }
        foreach (var mime in f.MimeTypes) { w.BeginStruct(); w.UInt32(1); w.String(mime); }
        w.EndArray(patterns);
    }

    /// <summary>A <c>Response(u response, a{sv} results)</c>, read: 0 chose, 1 cancelled, 2 ended otherwise.</summary>
    internal static FileChooserResult ReadResponse(DBusMessage response)
    {
        object[] body;
        try
        {
            if (response.Signature != "ua{sv}") return FileChooserResult.Failed($"the portal's Response carries '{response.Signature}', not 'ua{{sv}}'");
            body = response.ReadBody();
        }
        catch (DBusFormatException e) { return FileChooserResult.Failed($"the portal's Response is malformed: {e.Message}"); }

        switch ((uint)body[0])
        {
            case 0: break;
            case 1: return new FileChooserResult(FileChooserOutcome.Cancelled, [], null);
            default: return FileChooserResult.Failed("the portal ended the request without a choice");
        }

        var results = (Dictionary<object, object>)body[1];
        if (!results.TryGetValue("uris", out var value) || value is not DBusVariant { Signature: "as", Value: List<object> uris })
            return FileChooserResult.Failed("the portal's Response holds no uris");
        var paths = new List<string>(uris.Count);
        var skipped = new List<string>();
        foreach (string uri in uris.Cast<string>())
        {
            if (FileUris.ToLocalPath(uri) is { } path) paths.Add(path);
            else skipped.Add(uri);
        }
        if (paths.Count == 0 && skipped.Count > 0)
            return FileChooserResult.Failed("the portal chose nothing on this machine: " + string.Join(", ", skipped));
        return new FileChooserResult(FileChooserOutcome.Chosen, paths,
            skipped.Count == 0 ? null : "left out, not local files: " + string.Join(", ", skipped));
    }

    // The bus's errors for a name nobody owns or nothing could start, and for
    // a portal that has no FileChooser.
    private static bool IsAbsent(string? error) => error is not null && (
        error is "org.freedesktop.DBus.Error.ServiceUnknown"
            or "org.freedesktop.DBus.Error.NameHasNoOwner"
            or "org.freedesktop.DBus.Error.UnknownMethod"
            or "org.freedesktop.DBus.Error.UnknownInterface"
            or "org.freedesktop.DBus.Error.UnknownObject"
            or "org.freedesktop.DBus.Error.NoReply"
            or "org.freedesktop.DBus.Error.TimedOut"
        || error.StartsWith("org.freedesktop.DBus.Error.Spawn.", StringComparison.Ordinal));

    private static async Task<DBusConnection> ConnectAsync(string address, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(ConnectTimeout);
        return await DBusConnection.ConnectAsync(address, limit.Token).ConfigureAwait(false);
    }

    private static async Task<DBusMessage> CallAsync(DBusConnection bus, DBusMessage call, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(CallTimeout);
        return await bus.CallAsync(call, limit.Token).ConfigureAwait(false);
    }

    internal static async Task AddMatchAsync(DBusConnection bus, string path, CancellationToken ct)
    {
        var rule = new DBusWriter();
        rule.String($"type='signal',sender='{Desktop}',interface='{RequestInterface}',member='Response',path='{path}'");
        var reply = await CallAsync(bus, DBusMessage.Call(DBusConnection.Bus, "/org/freedesktop/DBus", DBusConnection.Bus, "AddMatch", "s", rule), ct).ConfigureAwait(false);
        if (reply.Type == DBusMessageType.Error) throw new IOException($"the bus refused the match rule: {reply.ErrorName} {reply.ErrorText()}");
    }

    // Closes the dialog of a request given up on. Best effort: the connection
    // closing a moment later ends the request as well.
    private static async Task CloseRequestAsync(DBusConnection bus, string handle)
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try { await bus.CallAsync(DBusMessage.Call(Desktop, handle, RequestInterface, "Close"), limit.Token).ConfigureAwait(false); }
        catch (Exception e) when (e is OperationCanceledException or IOException) { }
    }
}
