namespace Bibui.Core.Draw;

/// <summary>Axis-aligned rectangle in logical pixel coordinates, passed to native component renderers.</summary>
internal readonly record struct Frame(float X, float Y, float Width, float Height);

/// <summary>Width × height measurement returned by component Measure() methods.</summary>
internal readonly record struct Size(float Width, float Height);
