using System.Text.Json;

namespace Bpmn.Semantics.Tests;

/// <summary>Parses a JSON literal into a standalone element that outlives its document.</summary>
internal static class TestJson
{
    public static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
