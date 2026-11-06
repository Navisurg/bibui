using System.Text.Json.Serialization;

namespace Bibui.Automation;

// The test.* requests as they are written on the wire: one record per command, read
// by the JSON source generator. An app's own commands are not in here: the port hands
// those over as bytes, for the app to read, and lists them in test.ops.

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$cmd")]
[JsonDerivedType(typeof(EventsRequest), "test.events")]
[JsonDerivedType(typeof(TreeRequest), "test.tree")]
[JsonDerivedType(typeof(OpsRequest), "test.ops")]
[JsonDerivedType(typeof(ScreenshotRequest), "test.screenshot")]
internal abstract record TestRequest;

/// <summary>One frame's input.</summary>
internal sealed record EventsRequest(EventJson[] Events) : TestRequest;

/// <summary>The UI tree the frame laid out.</summary>
internal sealed record TreeRequest : TestRequest;

/// <summary>What the app can be asked to do: an entry per operation.</summary>
internal sealed record OpsRequest : TestRequest;

/// <summary>
/// The frame as a PNG, written by the app at <see cref="Path"/>: the whole frame, or
/// with <see cref="Id"/> only that node's rect.
/// </summary>
internal sealed record ScreenshotRequest(string Path, string? Id = null) : TestRequest;

/// <summary>
/// One event, as a window reports one: <c>kind</c> is <c>mouse_move</c>, <c>mouse_down</c>,
/// <c>mouse_up</c>, <c>scroll</c>, <c>key_down</c>, <c>key_up</c>, <c>key_char</c> or
/// <c>focus_lost</c>, and each kind reads the fields it needs. A struct: a request's
/// events are one array.
/// </summary>
internal readonly record struct EventJson(
    string Kind,
    float? X = null, float? Y = null,
    float? Dx = null, float? Dy = null,
    string? Button = null,
    string? Key = null,
    string? Char = null,
    string[]? Modifiers = null);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    AllowOutOfOrderMetadataProperties = true,
    RespectRequiredConstructorParameters = true,
    RespectNullableAnnotations = true)]
[JsonSerializable(typeof(TestRequest))]
internal sealed partial class TestRequestJson : JsonSerializerContext;
