/*
 * Copyright (c) 2018 Demerzel Solutions Limited
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
using System.Linq;
using Newtonsoft.Json;

namespace Nethermind.Evm
{
    public class TransactionTraceEntry
    {
        public TransactionTraceEntry()
        {
            Stack = new List<string>();
            Memory = new List<string>();
            Storage = new Dictionary<string, string>();
        }

        [JsonProperty("pc", Order = 0)]
        public long Pc { get; set; }

        [JsonProperty("op", Order = 1)]
        public string Operation { get; set; }

        [JsonProperty("gas", Order = 2)]
        public long Gas { get; set; }

        [JsonProperty("gasCost", Order = 3)]
        public long GasCost { get; set; }

        [JsonProperty("depth", Order = 4)]
        public int Depth { get; set; }

        [JsonProperty("stack", Order = 5)]
        public List<string> Stack { get; set; }

        [JsonProperty("memory", Order = 6)]
        public List<string> Memory { get; set; }

        [JsonIgnore]
        public Dictionary<string, string> Storage { get; set; }

        [JsonProperty("storage", Order = 7)]
        public Dictionary<string, string> SortedStorage => Storage.OrderBy(p => p.Key).ToDictionary(p => p.Key, p => p.Value);
    }
}