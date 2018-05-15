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
using System.Numerics;
using Newtonsoft.Json;

namespace Nethermind.Evm
{
    public class WrappedTransactionTrace
    {
        [JsonProperty("jsonRpc")]
        public string RpcVersion { get; set; }

        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("result")]
        public TransactionTrace Result { get; set; }
    }

    public class TransactionTrace
    {
        public TransactionTrace()
        {
            Entries = new List<TransactionTraceEntry>();
        }

        [JsonProperty("gas", Order = 0)]
        public BigInteger Gas { get; set; } // TODO: not implemented

        [JsonProperty("failed", Order = 1)]
        public bool Failed { get; set; } // TODO: not implemented

        [JsonProperty("returnValue", Order = 2)]
        public byte[] ReturnValue { get; set; } // TODO: not implemented

        [JsonProperty("structLogs", Order = 3)]
        public List<TransactionTraceEntry> Entries { get; set; }
    }
}