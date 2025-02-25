using System.Text.Json.Serialization;
using Nethermind.Serialization.Json;

namespace Nethermind.PatternAnalyzer.Plugin.Stats;

public class PatternAnalyzerTraceEntry
{

    [JsonPropertyName("pattern")]
    public required string Pattern { get; set; }

    [JsonPropertyName("bytes")]
    public required byte[] Bytes { get; set; }

    [JsonPropertyName("count")]
    [JsonConverter(typeof(ULongConverter))]
    public required ulong Count { get; set; }

//    // Constructor to initialize the required properties
//    public OpcodeStatsTraceEntry(string pattern, byte[] bytes, ulong count)
//    {
//        Pattern = pattern ?? throw new ArgumentNullException(nameof(pattern));
//        Bytes = bytes ?? throw new ArgumentNullException(nameof(bytes));
//        Count = count;
//    }
}


