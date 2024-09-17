using System.Collections.Generic;
using System.Text.Json.Serialization;
using Nethermind.Evm.CodeAnalysis.StatsAnalyzer;
using Nethermind.Serialization.Json;

namespace Nethermind.Evm.Tracing.OpcodeStats;

public class  OpcodeStatsTraceEntry
{
     public OpcodeStatsTraceEntry()
     {
     }

    [JsonPropertyName("pattern")]
    public string Pattern { get; set; }

    [JsonPropertyName("bytes")]
    public byte[] Bytes{ get; set; }

    [JsonPropertyName("count")]
    [JsonConverter(typeof(ULongConverter))]
    public ulong Count { get; set; }

}

