using System.Text.Json.Serialization;

namespace Bibui.Automation;

// The test.* requests as they are written on the wire: one record per command, read
// by the JSON source generator and exported as test.schema's half, so the names and
// arguments are said once. An app's own commands are not in here: the port hands
// those over as bytes, for the app to read.

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$cmd")]
[JsonDerivedType(typeof(EventsRequest), "test.events")]
[JsonDerivedType(typeof(TreeRequest), "test.tree")]
[JsonDerivedType(typeof(StateRequest), "test.state")]
[JsonDerivedType(typeof(SchemaRequest), "test.schema")]
[JsonDerivedType(typeof(ScreenshotRequest), "test.screenshot")]
internal abstract record TestRequest;

/// <summary>One frame's input.</summary>
internal sealed record EventsRequest(EventJson[] Events) : TestRequest;

/// <summary>The UI tree the frame laid out.</summary>
internal sealed record TreeRequest : TestRequest;

/// <summary>The app's state after the frame.</summary>
internal sealed record StateRequest : TestRequest;

/// <summary>The commands a client may send: these, and the app's.</summary>
internal sealed record SchemaRequest : TestRequest;

/// <summary>The frame as a PNG, written by the app at <see cref="Path"/>.</summary>
internal sealed record ScreenshotRequest(string Path) : TestRequest;

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
