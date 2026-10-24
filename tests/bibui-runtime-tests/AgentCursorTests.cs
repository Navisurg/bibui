using Bibui.Core;
using Bibui.Core.Runtime;
using Bibui.Core.Runtime.Platform;

/// <summary>AgentCursor, folded from an agent's window events.</summary>
static class AgentCursorTests
{
    static void Eq<T>(T expected, T actual, string message)
    {
        if (!Equals(expected, actual)) throw new Exception($"{message}: expected {expected} but got {actual}");
    }

    static readonly UiColor Blue = UiColor.Hex("#3B82F6");

    public static void Register(Action<string, Action> run)
    {
        run("AgentCursor.FromWindowEvents: pointer events move it, a press holds it and counts a click, a release lets go", () =>
        {
            var cursor = AgentCursor.Start("Agent", Blue);
            cursor = AgentCursor.FromWindowEvents(cursor, [WindowEvent.Mouse(WindowEventKind.MouseMove, 10, 20, MouseButton.Unknown)]);
            Eq((10f, 20f, false, 0), (cursor.X, cursor.Y, cursor.Pressed, cursor.Clicks), "a move");
            cursor = AgentCursor.FromWindowEvents(cursor, [WindowEvent.Mouse(WindowEventKind.MouseDown, 30, 40, MouseButton.Left), WindowEvent.KeyPress(Key.A)]);
            Eq((30f, 40f, true, 1), (cursor.X, cursor.Y, cursor.Pressed, cursor.Clicks), "a press, and a key that leaves it");
            cursor = AgentCursor.FromWindowEvents(cursor, [WindowEvent.Mouse(WindowEventKind.MouseUp, 30, 40, MouseButton.Left), WindowEvent.Wheel(50, 60, 0, -1)]);
            Eq((50f, 60f, false, 1), (cursor.X, cursor.Y, cursor.Pressed, cursor.Clicks), "a release, and a wheel that moves it");
            Eq(("Agent", Blue), (cursor.Name, cursor.Color), "its name and colour kept");
        });
    }
}
