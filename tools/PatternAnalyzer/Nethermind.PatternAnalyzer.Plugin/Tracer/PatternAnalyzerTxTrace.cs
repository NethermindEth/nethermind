
// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Serialization;

namespace Nethermind.PatternAnalyzer.Plugin.Tracer;

[JsonConverter(typeof(OpcodeStatsTraceConvertor))]
public class PatternAnalyzerTxTrace
{

    [JsonPropertyName("initialBlockNumber")]
    public long InitialBlockNumber { get; set; }
    [JsonPropertyName("currentBlockNumber")]
    public long CurrentBlockNumber { get; set; }
    [JsonPropertyName("errorPerItem")]
    public double ErrorPerItem { get; set; }
    [JsonPropertyName("confidence")]
    public double Confidence { get; set; }


    public PatternAnalyzerTxTrace() { }


    [JsonPropertyName("stats")]
    public List<PatternAnalyzerTraceEntry> Entries { get; set; } = new List<PatternAnalyzerTraceEntry>();


}
