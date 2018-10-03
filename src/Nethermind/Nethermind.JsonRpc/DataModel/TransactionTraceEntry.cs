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

namespace Nethermind.JsonRpc.DataModel
{
    public class TransactionTraceEntry : IJsonRpcResult
    {
        public long Pc { get; set; }
        public string Op { get; set; }
        public long Gas { get; set; }
        public long GasCost { get; set; }
        public int Depth { get; set; }
        public string Error { get; set; }
        public List<string> Stack { get; set; }
        public List<string> Memory { get; set; }
        public Dictionary<string, string> Storage { get; set; }

        public object ToJson()
        {
            return new
            {
                pc = Pc,
                op = Op,
                gas = Gas,
                gasCost = GasCost,
                error = Error,
                depth = Depth,
                stack = Stack,
                memory = Memory,
                storage = Storage.OrderBy(p => p.Key).ToDictionary(p => p.Key, p => p.Value)
            };
        }
    }
}