// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Newtonsoft.Json;

namespace Nethermind.State.Test.Runner
{
    public class StateTestTxTraceEntry
    {
        public StateTestTxTraceEntry()
        {
            Stack = new List<string>();
        }

        [JsonProperty(PropertyName = "pc")]
        public int Pc { get; set; }

        [JsonProperty(PropertyName = "op")]
        public byte Operation { get; set; }

        [JsonProperty(PropertyName = "gas")]
        public long Gas { get; set; }

        [JsonProperty(PropertyName = "gasCost")]
        public long GasCost { get; set; }

        [JsonProperty(PropertyName = "memory")]
        public string Memory { get; set; }

        [JsonProperty(PropertyName = "memSize")]
        public int MemSize { get; set; }

        [JsonProperty(PropertyName = "stack")]
        public List<string> Stack { get; set; }

        [JsonProperty(PropertyName = "depth")]
        public int Depth { get; set; }

        [JsonProperty(PropertyName = "refund")]
        public int Refund { get; set; }

        [JsonProperty(PropertyName = "opname")]
        public string? OperationName { get; set; }

        [JsonProperty(PropertyName = "error")]
        public string? Error { get; set; } = string.Empty;

        //        public Dictionary<string, string> Storage { get; set; }

        internal void UpdateMemorySize(int size)
        {
            MemSize = size;
        }
    }
}
