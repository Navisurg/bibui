using System.Text.Json;
using Bibui.Core;

namespace Bibui.Automation;

/// <summary>
/// The UI tree the last frame laid out, as JSON: what <c>test.tree</c> answers. Each
/// node says what it is, where it was laid out and what it holds, leaving out what
/// is empty or false:
/// <code>{"kind":"box","id":"add","x":10,"y":40,"w":80,"h":24,"clickable":true,"children":[{"kind":"text","x":..,"text":"Add one"}]}</code>
/// Written straight into the reply, on the loop, between frames.
/// </summary>
internal static class TestTree
{
    public static void Write(Utf8JsonWriter json, UiNode? root)
    {
        if (root is null) json.WriteNullValue();
        else Node(json, root);
    }

    private static void Node(Utf8JsonWriter json, UiNode node)
    {
        json.WriteStartObject();
        json.WriteString("kind", node switch { Box => "box", Text => "text", _ => "node" });
        if (node.Id is { } id) json.WriteString("id", id);
        json.WriteNumber("x", node.Rect.X);
        json.WriteNumber("y", node.Rect.Y);
        json.WriteNumber("w", node.Extent.X);
        json.WriteNumber("h", node.Extent.Y);
        if (node is Text text) json.WriteString("text", text.Content);
        if (node is Box box)
        {
            if ((box.Claims & InteractionKinds.Press) != 0) json.WriteBoolean("clickable", true);
            if ((box.Claims & InteractionKinds.Disabled) != 0) json.WriteBoolean("disabled", true);
        }
        if (node.FirstChild is not null)
        {
            json.WriteStartArray("children");
            for (var child = node.FirstChild; child is not null; child = child.Next == child ? null : child.Next)
                Node(json, child);
            json.WriteEndArray();
        }
        json.WriteEndObject();
    }
}
