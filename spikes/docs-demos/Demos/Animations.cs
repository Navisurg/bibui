// The docs site's Animations page: one small demo per idea, registered in
// Program.cs as "anim-*". Each is built centred in its canvas, so every size
// in here is pixels or the content's.
using System.Numerics;
using Bibui;
using Bibui.Runtime;
using Bibui.Theming;

namespace DocsDemos;

/// <summary>HotT and ActiveT: eased by the runtime, read by the build.</summary>
internal sealed class AnimHover
{
    private static readonly string[] Titles = ["Hover", "Press", "Lift"];
    private static readonly string[] Notes = ["HotT eases in", "ActiveT jumps to 1", "Style.Translate"];

    public void Build(BibuiRuntime runtime)
    {
        using (runtime.PushParent(runtime.RowFit()))
        {
            for (int i = 0; i < Titles.Length; i++)
            {
                if (i > 0) runtime.Gap(16f);
                Box card = runtime.BoxFromId(Titles[i], InteractionKinds.LeftPress);
                runtime.InteractionsFor(card);
                card.Px(150f, 96f);
                card.ChildLayoutAxis = LayoutAxis.Y;
                card.Style.Padding = EdgeInsets.All(14f);
                card.Style.CornerRadius = runtime.Theme.Radii.Lg;
                card.Style.BorderWidth = runtime.Theme.Borders.Width;
                card.Style.BorderColor = UiColor.Border;
                card.Style.BackgroundColor = UiColor.Accent.WithAlpha(0.05f + 0.2f * card.HotT + 0.15f * card.ActiveT);
                // Painted moved, not laid out moved: nothing around it shifts.
                card.Style.Translate = new Vector2(0f, -6f * card.HotT + 3f * card.ActiveT);
                using (runtime.PushParent(card))
                {
                    runtime.Text(Titles[i]);
                    runtime.Gap(6f);
                    runtime.Text(Notes[i], ComponentSize.Sm, TextColor.Secondary);
                    runtime.Gap(10f);
                    Box bar = runtime.Box().Px(120f * card.HotT + 2f, 3f);
                    bar.Style.BackgroundColor = UiColor.Accent;
                    bar.Style.CornerRadius = 1.5f;
                }
            }
        }
    }
}

/// <summary>runtime.Animate: a value that glides to its target, at the theme's speed.</summary>
internal sealed class AnimPanel
{
    private const string OpenLabel = "Open the panel";
    private const string CloseLabel = "Close the panel";
    private const string PanelLabel = "Sidebar";
    private const string BodyLabel = "The content gives way as the panel opens.";
    private bool _open;

    public void Build(BibuiRuntime runtime)
    {
        using var scope = runtime.PushParent(runtime.ColumnFit());
        if (runtime.Button("panel-toggle", _open ? CloseLabel : OpenLabel).Clicked) _open = !_open;
        runtime.Gap(16f);
        float open = runtime.Animate("panel", "width", _open ? 1f : 0f);

        using var row = runtime.PushParent(runtime.RowPx(420f, 120f));
        Box side = runtime.Box().Px(40f + 160f * open, 120f);
        side.ChildLayoutAxis = LayoutAxis.Y;
        side.Style.BackgroundColor = UiColor.Accent.WithAlpha(0.25f);
        side.Style.CornerRadius = runtime.Theme.Radii.Md;
        side.Style.Padding = EdgeInsets.All(12f);
        using (runtime.PushParent(side))
        {
            Text label = runtime.Text(PanelLabel, ComponentSize.Sm);
            label.Style.Opacity = open;
        }

        Box body = runtime.Box().FillX().Px(height: 120f);
        body.Style.BackgroundColor = UiColor.SurfaceRaised;
        body.Style.CornerRadius = runtime.Theme.Radii.Md;
        body.Style.Padding = EdgeInsets.All(12f);
        using (runtime.PushParent(body))
            runtime.Text(BodyLabel, ComponentSize.Sm, TextColor.Secondary);

    }
}

/// <summary>Three ways to travel to the same target, side by side.</summary>
internal sealed class AnimTransitions
{
    private const string Go = "Move them";
    private const float Track = 300f;
    private bool _right;

    public void Build(BibuiRuntime runtime)
    {
        using var scope = runtime.PushParent(runtime.ColumnFit());
        if (runtime.Button("transitions-go", Go).Clicked) _right = !_right;
        float target = _right ? 1f : 0f;

        float exp = runtime.Animate("t-exp", "x", target);
        float timed = runtime.Animate("t-timed", "x", target, Transition.Timed(0.6f, Easing.Emphasized));
        float spring = runtime.Animate("t-spring", "x", target, Transition.OnSpring(Bibui.Spring.Bouncy));

        runtime.Gap(16f);
        Lane(runtime, "Exponential (default)", exp);
        runtime.Gap(10f);
        Lane(runtime, "Timed, Emphasized", timed);
        runtime.Gap(10f);
        Lane(runtime, "Spring, Bouncy", spring);
    }

    private static void Lane(BibuiRuntime runtime, string name, float t)
    {
        using var lane = runtime.PushParent(runtime.RowFit());
        using (runtime.PushParent(runtime.ColumnPx(170f, 28f)))
        using (runtime.Centred())
            runtime.Text(name, ComponentSize.Sm, TextColor.Secondary);

        Box track = runtime.Box().Px(Track + 20f, 28f);
        track.Style.BackgroundColor = UiColor.SurfaceRaised;
        track.Style.CornerRadius = 14f;
        using (runtime.PushParent(track))
        {
            Box dot = runtime.Box().Px(20f, 20f).At(4f + (Track - 8f) * t, 4f);
            dot.Style.BackgroundColor = UiColor.Accent;
            dot.Style.CornerRadius = 10f;
        }
    }
}

/// <summary>PresenceList and CollapseBox: rows that stay while they leave.</summary>
internal sealed class AnimPresence
{
    private const string Add = "Add a row";
    private const int Max = 5;
    private static readonly Func<int, string> KeyOf = static n => n.ToString(System.Globalization.CultureInfo.InvariantCulture);
    private readonly List<int> _rows = [1, 2, 3];
    private int _next = 4;

    public void Build(BibuiRuntime runtime)
    {
        var column = runtime.ColumnWidth(340f);
        using (runtime.PushParent(column))
        {
            if (runtime.Button("presence-add", Add, disabled: _rows.Count >= Max).Clicked) _rows.Add(_next++);
            runtime.Gap(12f);

            int? remove = null;
            foreach (var row in runtime.PresenceList("rows", _rows, KeyOf, stagger: 0.03f))
            {
                Box holder = runtime.CollapseBox("hold-" + row.Key, row.Progress).FillX();
                using (runtime.PushParent(holder))
                {
                    Box line = runtime.BoxFromId("row-" + row.Key,
                        row.Exiting ? InteractionKinds.None : InteractionKinds.LeftPress);
                    if (runtime.InteractionsFor(line).Clicked && !row.Exiting) remove = row.Item;
                    line.FillX().Px(height: 40f);
                    line.ChildLayoutAxis = LayoutAxis.X;
                    line.Style.Padding = EdgeInsets.Symmetric(horizontal: 14f, vertical: 0f);
                    line.Style.CornerRadius = runtime.Theme.Radii.Md;
                    line.Style.BackgroundColor = UiColor.SurfaceRaised;
                    line.Style.HoverBackgroundColor = UiColor.SurfaceRaisedHover;
                    PresenceStyle.SlideFromRight(32f).ApplyTo(line.Style, row.Progress);
                    using (runtime.PushParent(line))
                    using (runtime.Centred())
                        runtime.Text($"Row {row.Item}: click to remove it", ComponentSize.Sm);
                    runtime.Gap(8f);
                }
            }
            if (remove is int gone) _rows.Remove(gone);
        }
    }
}

/// <summary>SetTheme with a crossfade: every colour travels to the new theme.</summary>
internal sealed class AnimTheme
{
    private const string Light = "Light theme";
    private const string Notify = "Notifications";
    private const string Volume = "Volume";
    private const string Save = "Save";
    private bool _light;
    private bool _notify = true;
    private float _volume = 0.6f;

    public void Build(BibuiRuntime runtime)
    {
        var column = runtime.ColumnWidth(300f);
        using (runtime.PushParent(column))
        {
            if (runtime.Switch("theme-light", _light, Light).Clicked)
            {
                _light = !_light;
                runtime.SetTheme(_light ? ThemeLoader.BibuiLight() : ThemeLoader.BibuiDark(),
                    Transition.Timed(0.45f, Easing.Standard));
            }
            runtime.Gap(16f);
            if (runtime.Checkbox("theme-notify", _notify, Notify).Clicked) _notify = !_notify;
            runtime.Gap(16f);
            _volume = runtime.Slider("theme-volume", _volume, label: Volume).Value;
            runtime.Gap(16f);
            runtime.Button("theme-save", Save);
        }
    }
}
