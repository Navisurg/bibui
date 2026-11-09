# Bibui.Platform.Portal

The desktop's own dialogs for a bibui app, through xdg-desktop-portal: the
file chooser GNOME, KDE or the compositor's portal draws, asked over the
session bus. It works with any bibui window and holds no thread while the
dialog is open, so the frame loop keeps running. Linux only.

```csharp
var paths = await FileChooser.OpenAsync(new FileChooserOptions
{
    Title = "Attach files",
    Multiple = true,
    Filters = [new FileFilter("Images", "*.png", "*.jpg")],
});
// Local paths, or null when cancelled or there is no portal.
```

`Directory = true` chooses folders. `FileChooser.ChooseAsync` says how a
request ended and why (`Chosen`, `Cancelled`, `Unavailable`, `Failed`); without
a session bus or a portal it comes back `Unavailable` and never throws.
`FileChooser.VersionAsync()` asks the portal's version without opening a
dialog.

It speaks D-Bus over the session bus socket itself: no NuGet dependency, no
native library, no reflection, so it publishes with Native AOT.

Part of bibui, a code-first native UI library for .NET:
<https://github.com/Navisurg/bibui>. Licensed under Apache-2.0.
