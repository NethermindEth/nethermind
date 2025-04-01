using System.Text.Json.Serialization;
using Nethermind.Serialization.Json;

namespace Nethermind.PatternAnalyzer.Plugin.Tracer.Call;

public class CallAnalyzerTraceEntry
{
    [JsonPropertyName("address")] public required string Address { get; set; }

    [JsonPropertyName("count")]
    [JsonConverter(typeof(ULongConverter))]
    public required ulong Count { get; set; }
}
