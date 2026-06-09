using System.Text.Json.Serialization;
using GameBoy.Debug.Core;

namespace GameBoy.Debug.Mcp;

public sealed record ToolError([property: JsonPropertyName("error")] DebugError Error);
