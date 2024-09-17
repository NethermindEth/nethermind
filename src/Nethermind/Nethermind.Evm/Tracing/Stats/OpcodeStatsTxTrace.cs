
// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using Nethermind.Evm.Tracing.GethStyle.Custom;


namespace Nethermind.Evm.Tracing.OpcodeStats;

[JsonConverter(typeof(OpcodeStatsTraceConvertor))]
public class OpcodeStatsTxTrace
{

    public long  InitialBlockNumber { get; set; }
    public long  CurrentBlockNumber { get; set; }
    public double ErrorPerItem { get; set; }
    public double Confidence { get; set;}


    public OpcodeStatsTxTrace() { }


    public List<OpcodeStatsTraceEntry> Entries { get; set; } = new List<OpcodeStatsTraceEntry>();


}
