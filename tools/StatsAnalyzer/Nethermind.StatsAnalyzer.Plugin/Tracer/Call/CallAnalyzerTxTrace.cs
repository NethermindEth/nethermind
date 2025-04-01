// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Serialization;

namespace Nethermind.StatsAnalyzer.Plugin.Tracer.Call;

[JsonConverter(typeof(CallAnalyzerTxTraceConvertor))]
public class CallAnalyzerTxTrace
{
    [JsonPropertyName("initialBlockNumber")]
    public long InitialBlockNumber { get; set; }

    [JsonPropertyName("currentBlockNumber")]
    public long CurrentBlockNumber { get; set; }


    [JsonPropertyName("stats")] public List<CallAnalyzerTraceEntry> Entries { get; set; } = new();
}
