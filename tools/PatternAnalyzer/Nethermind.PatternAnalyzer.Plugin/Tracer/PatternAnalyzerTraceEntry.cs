using System.Text.Json.Serialization;
using Nethermind.Serialization.Json;

namespace Nethermind.PatternAnalyzer.Plugin.Tracer;

public class PatternAnalyzerTraceEntry
{
    [JsonPropertyName("pattern")] public required string Pattern { get; set; }

    [JsonPropertyName("bytes")] public required byte[] Bytes { get; set; }

    [JsonPropertyName("count")]
    [JsonConverter(typeof(ULongConverter))]
    public required ulong Count { get; set; }
}
