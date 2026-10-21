# Badge

A pill as large as its short label plus padding, in one of five variants, with
an optional leading dot. It takes nothing.

| Default | Primary | Success, dot | Destructive | Outline | Outline, dot |
|---|---|---|---|---|---|
| ![badge-default](images/badge-default.png) | ![badge-primary](images/badge-primary.png) | ![badge-success-dot](images/badge-success-dot.png) | ![badge-destructive](images/badge-destructive.png) | ![badge-outline](images/badge-outline.png) | ![badge-outline-success-dot](images/badge-outline-success-dot.png) |

Fixtures: `badge-default`, `badge-primary`, `badge-success-dot`,
`badge-destructive`, `badge-outline` and `badge-outline-success-dot`.

## Build

```cs
public static Box Build(
    BibuiRuntime runtime,
    ReadOnlySpan<char> label,
    BadgeVariant variant = BadgeVariant.Default,
    bool leadingDot = false)

public enum BadgeVariant { Default, Primary, Success, Destructive, Outline }
```

| Parameter | Meaning |
|---|---|
| `label` | What it reads, at the small size. |
| `variant` | Its colours (see below). |
| `leadingDot` | A bullet before the label, in the variant's dot colour. |

## Claims and reports

None. It returns a keyless `Box`, so it needs no id.

## State the caller owns

None.

## Theme tokens

| Variant | Fill | Label | Dot |
|---|---|---|---|
| `Default` | `SurfaceRaised` | `TextPrimary` | `TextSecondary` |
| `Primary` | `Accent` | `AccentForeground` | `Accent` |
| `Success` | `SuccessBg` | `Success` | `Success` |
| `Destructive` | `Destructive` | `DestructiveForeground` | `Destructive` |
| `Outline` | none, with a `Border` edge `runtime.Theme.Borders.Width` wide | `TextPrimary` | `TextSecondary` |

## Example

```csharp
using Bibui.Core;
using Bibui.Core.Runtime;

sealed class SyncStatus
{
    const string InSync = "In sync";
    const string Failed = "Sync failed";

    public bool Failing { get; set; }   // the caller's state, shown

    public UiNode Build(BibuiRuntime runtime) => Failing
        ? Badge.Build(runtime, Failed, BadgeVariant.Destructive)
        : Badge.Build(runtime, InSync, BadgeVariant.Success, leadingDot: true);
}
```
