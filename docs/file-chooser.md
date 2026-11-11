# The file chooser

A bibui app asks for files, and where to save one, the way any Linux desktop app does: through
xdg-desktop-portal's `org.freedesktop.portal.FileChooser`, which shows the
desktop's own dialog (GNOME's, KDE's, or the one the compositor's portal
draws), in the person's language, with their bookmarks and recent files. The
package is `Bibui.Platform.Portal`. It works with any window, Silk or Wayland,
and publishes with Native AOT: it speaks D-Bus over the session bus socket
itself, with no dependency and no reflection.

```bash
dotnet add package Bibui.Platform.Portal
```

```csharp
using Bibui.Platform.Portal;

var paths = await FileChooser.OpenAsync(new FileChooserOptions
{
    Title = "Attach files",
    AcceptLabel = "Attach",
    Multiple = true,
    Filters = [new FileFilter("Images", "*.png", "*.jpg") { MimeTypes = ["image/*"] }],
});
if (paths is not null)
    foreach (var path in paths) Attach(path);   // local paths, /home/a/My Notes.md
```

`OpenAsync` returns the chosen files as local paths, or null when the person
cancelled or there is no file chooser to ask. A folder is chosen with
`Directory = true`:

```csharp
var folder = await FileChooser.OpenAsync(new() { Title = "Open a project", Directory = true });
```

## Saving

`SaveAsync` asks where to save a file and returns its local path, or null.
The dialog asks whether to replace a file that is already there; the app
writes the file itself:

```csharp
var path = await FileChooser.SaveAsync(new SaveFileOptions
{
    Title = "Save the notes",
    CurrentName = "notes.md",
    Filters = [new FileFilter("Markdown", "*.md") { MimeTypes = ["text/markdown"] }],
});
if (path is not null) File.WriteAllText(path, notes);
```

For "Save as…" on a file that has a path, `CurrentFile` opens the dialog
beside it, with its name. `ChooseSaveAsync` takes the same options and says
how the request ended, as `ChooseAsync` does.

## From a frame

The dialog is the portal's, in another process: the call holds no thread
while it is open, and the frame loop keeps running under it. The task
completes on a thread-pool thread, so hand the result to the next build and
ask for a frame; `runtime.RequestFrame()` is safe from any thread:

```csharp
Task<IReadOnlyList<string>?>? picking = null;

void Build()
{
    if (runtime.Button("attach", "Attach…").Clicked && picking is null)
    {
        picking = FileChooser.OpenAsync(new() { Title = "Attach files", Multiple = true });
        picking.ContinueWith(_ => runtime.RequestFrame());
    }
    if (picking is { IsCompleted: true })
    {
        if (picking.Result is { } paths) attachments.AddRange(paths);
        picking = null;
    }
}
```

Cancelling the token given to `OpenAsync` or `SaveAsync` closes the dialog
and throws `OperationCanceledException`, as cancelling any .NET call does.

## Why nothing came back

`FileChooser.ChooseAsync` takes the same options and says how the request
ended (`ChooseSaveAsync` for a save): `Outcome` is `Chosen` (with `Paths`),
`Cancelled`, `Unavailable` or `Failed`, and `Reason` says why in words for a
log.

- **Unavailable**: there is no session bus (`DBUS_SESSION_BUS_ADDRESS` unset and
  no `$XDG_RUNTIME_DIR/bus`), no portal on it, or a portal with no
  FileChooser behind it, as on a desktop without a portal backend installed. It
  never throws for any of these; an app hides its "Attach" button or offers a
  path field instead.
- **Failed**: the portal was asked and refused, ended the request without a
  choice, or the bus went away while the dialog was open.

`FileChooser.VersionAsync()` is the portal's FileChooser version, or null when
there is none. It opens no dialog, so an app can ask it at start-up to decide
what to offer. `Directory` needs version 3 and `CurrentFolder` version 4; an
older portal ignores them.

## Options

`FileChooserOptions`, for opening:

| Option | What it does |
|---|---|
| `Title` | The dialog's title. |
| `AcceptLabel` | The accept button's label; the portal's own ("Open") when empty. |
| `Multiple` | More than one file, or folder, may be chosen. |
| `Directory` | Folders are chosen instead of files. |
| `Filters` | `FileFilter`s by glob (`*.md`) and MIME type (`MimeTypes = ["text/markdown"]`), in order. |
| `CurrentFilter` | The filter selected when the dialog opens. |
| `CurrentFolder` | The folder the dialog opens in. |
| `Modal` | Modal to `ParentWindow`; true unless set. |
| `ParentWindow` | The window the dialog belongs to, `wayland:HANDLE` or `x11:XID`. |

`SaveFileOptions` has `Title`, `AcceptLabel` ("Save" when empty), `Filters`,
`CurrentFilter`, `CurrentFolder`, `Modal` and `ParentWindow` as above, and:

| Option | What it does |
|---|---|
| `CurrentName` | The file name the dialog suggests. |
| `CurrentFile` | The file being saved again: the dialog opens beside it, with its name. |

The dialog is unparented unless `ParentWindow` names a window: the compositor
places it on its own, not over the app. On Wayland a window's handle is
exported through the xdg-foreign protocol, which bibui's Wayland window does
not do yet; until it does, leave `ParentWindow` empty.

A path comes back as the portal gives it. For an app outside a sandbox that is
the file's own path; for a Flatpak app it is the document portal's
(`/run/user/1000/doc/…`), which the app can read. A URI that is not a local
file, as a remote location chosen in the dialog can be, is left out and named
in `Reason`.
