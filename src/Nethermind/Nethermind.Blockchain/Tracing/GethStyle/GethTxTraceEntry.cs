// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using Nethermind.Core;
using Nethermind.Int256;
using Nethermind.Serialization.Json;

namespace Nethermind.Blockchain.Tracing.GethStyle;

public class GethTxTraceEntry
{
    public GethTxTraceEntry()
    {
    }

    [JsonPropertyName("pc")]
    [JsonConverter(typeof(LongRawJsonConverter))]
    public long ProgramCounter { get; set; }

    [JsonPropertyName("op")]
    public string? Opcode { get; set; }

    [JsonConverter(typeof(ULongRawJsonConverter))]
    public ulong Gas { get; set; }

    [JsonConverter(typeof(ULongRawJsonConverter))]
    public ulong GasCost { get; set; }

    public int Depth { get; set; }

    public string? Error { get; set; }

    [JsonPropertyName("refund")]
    [JsonConverter(typeof(LongRawJsonConverter))]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? Refund { get; set; }

    [JsonConverter(typeof(StackHexConverter))]
    public ReadOnlyMemory<byte>? Stack { get; set; }

    [JsonConverter(typeof(MemoryHexConverter))]
    public ReadOnlyMemory<byte>? Memory { get; set; }

    [JsonConverter(typeof(StorageHexConverter))]
    public IDictionary<UInt256, UInt256>? Storage { get; set; }

    [JsonIgnore]
    internal (Address Address, UInt256 Key, UInt256 Value)? StorageDelta { get; set; }

    [JsonPropertyName("returnData")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ReturnData { get; set; }

    internal virtual void UpdateMemorySize(ulong size) { }
}
