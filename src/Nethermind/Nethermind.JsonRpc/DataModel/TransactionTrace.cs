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
using System.Numerics;
using Nethermind.Evm;

namespace Nethermind.JsonRpc.DataModel
{
    public class TransactionTrace : IJsonRpcResult
    {
        public BigInteger Gas { get; set; }
        public bool Failed { get; set; }
        public string ReturnValue { get; set; }
        public IEnumerable<TransactionTraceEntry> StructLogs { get; set; }
        public StorageTrace StorageTrace { get; set; }
        
        public object ToJson()
        {
            return new
            {
                gas = Gas,
                failed = Failed,
                returnValue = ReturnValue,
                structLogs = StructLogs?.Select(x => x.ToJson()).ToArray(),
                storageTrace = StorageTrace.ToJson()
            };
        }
    }
}