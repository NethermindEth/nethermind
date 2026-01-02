// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Nethermind.Test.Runner
{
    public class StateTestTxTraceEntry
    {
        public StateTestTxTraceEntry()
        {
            Stack = new List<string>();
        }

        [JsonPropertyName("pc")]
        public int Pc { get; set; }

        [JsonPropertyName("section")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public int Section { get; set; }

        [JsonPropertyName("op")]
        public byte Operation { get; set; }

        [JsonPropertyName("gas")]
        public long Gas { get; set; }

        [JsonPropertyName("gasCost")]
        public long GasCost { get; set; }

        [JsonPropertyName("memory")]
        public string Memory { get; set; }

        [JsonPropertyName("memSize")]
        public int MemSize { get; set; }

        [JsonPropertyName("stack")]
        public List<string> Stack { get; set; }

        [JsonPropertyName("depth")]
        public int Depth { get; set; }

        [JsonPropertyName("functionDepth")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public int FunctionDepth { get; set; }

        [JsonPropertyName("refund")]
        public int Refund { get; set; }

        [JsonPropertyName("opName")]
        public string? OperationName { get; set; }

        [JsonPropertyName("error")]
        public string? Error { get; set; } = string.Empty;

        //        public Dictionary<string, string> Storage { get; set; }

        internal void UpdateMemorySize(int size)
        {
            MemSize = size;
        }
    }
}
