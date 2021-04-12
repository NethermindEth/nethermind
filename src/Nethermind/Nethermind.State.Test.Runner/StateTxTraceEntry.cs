/*
 * Copyright (c) 2021 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

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
        public ulong MemSize { get; set; }

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

        internal void UpdateMemorySize(ulong size)
        {
            MemSize = size;
        }
    }
}
