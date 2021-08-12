//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

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
