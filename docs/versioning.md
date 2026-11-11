# Versions and compatibility

What a bibui version number promises, which API the promise covers, and how
a part of that API is retired.

## Version numbers

bibui does not follow [Semantic Versioning](https://semver.org/spec/v2.0.0.html)
for now. Every public package (`Bibui`, `Bibui.Core`, `Bibui.Text`, `Bibui.Glyphs`, `Bibui.Gl`, `Bibui.Testing`,
`Bibui.Raster.Skia`, `Bibui.Raster.Managed`, `Bibui.Raster.Managed.Backend`,
`Bibui.Platform.Silk`, `Bibui.Platform.Wayland`, `Bibui.Platform.Browser` and
the five `Bibui.App` packages) is released together, at the same version, from one tag. Depend on one version of all of them.

Given a version `MAJOR.MINOR.PATCH`:

- **MINOR** is the ordinary release: one more each time (1.1.0, 1.2.0, ...).
  It may add, change or remove public API. Every change that breaks code that
  compiled and worked before is marked **Breaking** in the changelog, with
  what to use instead.
- **PATCH** fixes a bug without changing the public API. Upgrading needs no
  change to your code.
- **MAJOR** is not raised for a breaking change. It is kept for when bibui
  returns to semantic versioning, or for a change large enough to call for it.

Pre-releases carry a suffix: `0.10.0-preview.1`, `1.0.0-rc.1`. A pre-release
promises nothing about the API it adds; it may change before the release.

The first public release is **0.9.0**. Before it, bibui was developed and used
privately, and the versions it carried then were never published.

**1.0.0** was released under the full semantic-versioning promise. From
**1.1.0** on, minor releases change and remove public API, as `0.y.z`
releases did before 1.0.0. Read the changelog's **Changed** and **Removed**
sections before upgrading across a minor version.

## What counts as public API

The public API is every public and protected type and member of the public
packages, with its signature and attributes. A change to it decides what the
changelog says, and what the release may be:

| The change | The changelog | The release |
|---|---|---|
| none | **Fixed** (or **Changed**, for a new behaviour) | patch or minor |
| only additions, or `[Obsolete]` added | **Added**, **Deprecated** | minor |
| a removal, or a changed signature | **Removed**, **Changed**, marked **Breaking** | minor |

Also public API, and so a breaking change when it changes:

- the documented behaviour of that API: what a component returns, what an
  ask consumes, what the layout solver does with a size;
- the theme file format read by `ThemeFile`, and the names of the themes
  bibui ships;
- the supported platforms and target framework (today Linux on .NET 10, and
  the browser through .NET 10's WebAssembly), and
  the native libraries a package needs at run time. Dropping a platform or a
  target framework, or requiring a new native library, is a breaking change;
- Native AOT compatibility of the packages that promise it (the Wayland host
  and the managed backend).

Not part of it:

- anything `internal`, even when a test reaches it through
  `InternalsVisibleTo`;
- exact pixels. Rendering is deterministic for one build of bibui, but a
  minor or patch release may change what a component looks like (a fixed
  anti-aliasing edge, a theme colour, a new default). The changelog says when
  it does;
- environment variables for diagnostics (`BIBUI_GPU`, `BIBUI_PROFILE`,
  `BIBUI_DAMAGE_TRACE`, ...), performance characteristics, and the text of
  exception messages;
- samples, tests and tools in this repository.

## Deprecation before removal

Where it costs little, public API is deprecated before it is removed:

1. **Deprecate it.** Mark the member `[Obsolete("Use X instead.")]` as a
   warning, not an error, keep it working, and record it under
   **Deprecated** in `CHANGELOG.md` with what replaces it. The replacement
   ships in the same release or earlier.
2. **Remove it in a later minor release**, listed under **Removed** in the
   changelog and marked **Breaking**.

API may also be removed or changed without a deprecated release first. The
changelog then says what to use instead, and marks it **Breaking**.

## How a release is made

A release is a tag `vMAJOR.MINOR.PATCH` on `main`; the release workflow packs
every public package at that version and publishes it to nuget.org. The
version in `Directory.Build.props` is the next release, and changes in the pull
request that prepares it, together with the changelog's new section.
