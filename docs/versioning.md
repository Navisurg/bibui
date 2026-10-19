# Versions and compatibility

What a bibui version number promises, which API the promise covers, and how
a part of that API is retired.

## Semantic versioning

bibui follows [Semantic Versioning 2.0.0](https://semver.org/spec/v2.0.0.html).
Every public package (`Bibui`, `Bibui.Gl`, `Bibui.Testing`,
`Bibui.Raster.Skia`, `Bibui.Raster.Managed`, `Bibui.Raster.Managed.Backend`,
`Bibui.Platform.Silk`, `Bibui.Platform.Wayland` and the five `Bibui.App`
packages) is released together, at the same version, from one tag. Depend on one version of all of them.

Given a version `MAJOR.MINOR.PATCH`:

- **PATCH** fixes a bug without changing the public API. Upgrading needs no
  change to your code.
- **MINOR** adds to the public API, or deprecates part of it, without breaking
  anything that compiled and worked before. Upgrading needs no change to your
  code; you may get new `[Obsolete]` warnings.
- **MAJOR** may remove or change public API. Everything it removes was
  deprecated in an earlier minor release first (below), and the changelog
  says what to use instead.

Pre-releases carry a suffix: `0.10.0-preview.1`, `1.0.0-rc.1`. A pre-release
promises nothing about the API it adds; it may change before the release.

The first public release is **0.9.0**. Before it, bibui was developed and used
privately, and the versions it carried then were never published.

**Until 1.0.0**, as semantic versioning allows for `0.y.z`, a **minor** release
(0.9 → 0.10) may change or remove public API; a **patch** release (0.9.0 →
0.9.1) still never does. Every such change is listed in the changelog with
what to use instead, and is deprecated first wherever that is practical.
1.0.0 starts the full promise above, once the API has settled with real use.

## What counts as public API

The public API is every public and protected type and member of the public
packages, with its signature and attributes. A change to it decides the
version bump:

| The change | The release |
|---|---|
| none | patch (or minor, for a new behaviour that changes no signature) |
| only additions, or `[Obsolete]` added | minor |
| a removal, or a changed signature | major |

Also part of the promise:

- the documented behaviour of that API: what a component returns, what an
  ask consumes, what the layout solver does with a size;
- the theme file format read by `ThemeFile`, and the names of the themes
  bibui ships;
- the supported platforms and target framework (today Linux on .NET 10), and
  the native libraries a package needs at run time. Dropping a platform or a
  target framework, or requiring a new native library, is a major change;
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

Public API is never removed or changed incompatibly without warning:

1. **Deprecate in a minor release.** Mark the member `[Obsolete("Use X
   instead. Removed in 3.0.")]` as a warning, not an error, keep it working,
   and record it under **Deprecated** in `CHANGELOG.md` with what replaces it.
   The replacement ships in the same release or earlier.
2. **Keep it for at least one minor release.** A deprecated member stays in
   every release of that major version.
3. **Remove it in the next major release**, listed under **Removed** in the
   changelog.

Where a change cannot be deprecated first (a security fix, a behaviour that
was plainly a bug), the changelog says why, and the release is still a major
one if it breaks the API.

## How a release is made

A release is a tag `vMAJOR.MINOR.PATCH` on `main`; the release workflow packs
every public package at that version and publishes it to nuget.org. The
version in `Directory.Build.props` is the next release, and changes in the pull
request that prepares it, together with the changelog's new section.
