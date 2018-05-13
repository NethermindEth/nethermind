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
using Newtonsoft.Json;

namespace Nethermind.Evm
{
    public class TransactionTraceEntry
    {
        public TransactionTraceEntry()
        {
            Stack = new List<byte[]>();
            Memory = new List<byte[]>();
            Storage = new List<byte[]>();
        }

        [JsonProperty("op")]
        public string Operation { get; set; }

        [JsonProperty("pc")]
        public int Pc { get; set; }

        [JsonProperty("gas")]
        public long Gas { get; set; }

        [JsonProperty("gaCost")]
        public long GasCost { get; set; }

        [JsonProperty("depth")]
        public int Depth { get; set; }

        [JsonProperty("stack")]
        public List<byte[]> Stack { get; set; }

        [JsonProperty("memory")]
        public List<byte[]> Memory { get; set; }

        [JsonProperty("storage")]
        public List<byte[]> Storage { get; set; }
    }
}