// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;

namespace Nethermind.Evm.Tracing.GethStyle
{
    public class GethTxTraceEntry
    {
        public GethTxTraceEntry()
        {
            Stack = new List<string>();
            Memory = new List<string>();
        }

        public long Pc { get; set; }

        [JsonProperty(PropertyName = "op")]
        public string? Operation { get; set; }

        public long Gas { get; set; }

        public long GasCost { get; set; }

        public int Depth { get; set; }

        public List<string>? Stack { get; set; }

        public string? Error { get; set; }

        public List<string>? Memory { get; set; }

        public Dictionary<string, string>? Storage { get; set; }

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
}
