
// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Serialization;

namespace Nethermind.PatternAnalyzer.Plugin.Stats;

[JsonConverter(typeof(OpcodeStatsTraceConvertor))]
public class PatternAnalyzerTxTrace
{

    public long InitialBlockNumber { get; set; }
    public long CurrentBlockNumber { get; set; }
    public double ErrorPerItem { get; set; }
    public double Confidence { get; set; }


    public PatternAnalyzerTxTrace() { }


    public List<PatternAnalyzerTraceEntry> Entries { get; set; } = new List<PatternAnalyzerTraceEntry>();


}
