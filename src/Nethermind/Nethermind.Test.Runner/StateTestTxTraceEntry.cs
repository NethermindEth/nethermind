// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Nethermind.Test.Runner
{
    public class StateTestTxTraceEntry
    {
        public StateTestTxTraceEntry() => Stack = [];

        public int Pc { get; set; }

        [JsonPropertyName("op")]
        public byte Operation { get; set; }

        public ulong Gas { get; set; }

        public ulong GasCost { get; set; }

        public string Memory { get; set; }

        public int MemSize { get; set; }

        public List<string> Stack { get; set; }

        public int Depth { get; set; }

        public int Refund { get; set; }

        [JsonPropertyName("opName")]
        public string? OperationName { get; set; }

        public string? Error { get; set; } = string.Empty;

        internal void UpdateMemorySize(int size) => MemSize = size;
    }
}
