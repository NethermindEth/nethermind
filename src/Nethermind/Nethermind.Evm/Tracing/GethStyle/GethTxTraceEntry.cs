// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nethermind.Evm.Tracing.GethStyle
{
    public class GethTxTraceEntry
    {
        public GethTxTraceEntry()
        {
            Stack = new List<string>();
            Memory = new List<string>();
        }

        [JsonConverter(typeof(LongRawJsonConverter))]
        public long Pc { get; set; }

        [JsonPropertyName("op")]
        public string? Operation { get; set; }

        [JsonConverter(typeof(LongRawJsonConverter))]
        public long Gas { get; set; }

        [JsonConverter(typeof(LongRawJsonConverter))]
        public long GasCost { get; set; }

        public int Depth { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
        public string? Error { get; set; }

        public List<string>? Stack { get; set; }

        public List<string>? Memory { get; set; }

        public Dictionary<string, string>? Storage { get; set; }

        [JsonIgnore]
        public Dictionary<string, string>? SortedStorage => Storage?.OrderBy(p => p.Key).ToDictionary(p => p.Key, p => p.Value);

        internal void UpdateMemorySize(ulong size)
        {
            // Geth's approach to memory trace is to show empty memory spaces on entry for the values that are being set by the operation
            Memory ??= new List<string>();

            int missingChunks = (int)((size - (ulong)Memory.Count * EvmPooledMemory.WordSize) / EvmPooledMemory.WordSize);
            for (int i = 0; i < missingChunks; i++)
            {
                Memory.Add("0000000000000000000000000000000000000000000000000000000000000000");
            }
        }
    }
    public class LongRawJsonConverter : JsonConverter<long>
    {
        public override long Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options) => throw new NotImplementedException();

        public override void Write(
            Utf8JsonWriter writer,
            long value,
            JsonSerializerOptions options)
        {
            writer.WriteNumberValue(value);
        }
    }
}
