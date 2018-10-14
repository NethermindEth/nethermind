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
    public class TransactionReceipt : IJsonRpcResult
    {
        public Data TransactionHash { get; set; }
        public Quantity TransactionIndex { get; set; }
        public Data BlockHash { get; set; }
        public Quantity BlockNumber { get; set; }
        public Quantity CumulativeGasUsed { get; set; }
        public Quantity GasUsed { get; set; }
        public Data From { get; set; }
        public Data To { get; set; }
        public Data ContractAddress { get; set; }
        public IEnumerable<Log> Logs { get; set; }
        public Data LogsBloom { get; set; }
        public Data Root { get; set; }
        public Quantity Status { get; set; }

        public object ToJson()
        {
            return new
            {
                transactionHash = TransactionHash?.ToJson(),
                transactionIndex = TransactionIndex?.ToJson(),
                blockHash = BlockHash?.ToJson(),
                blockNumber = BlockNumber?.ToJson(),
                cumulativeGasUsed = CumulativeGasUsed?.ToJson(),
                gasUsed = GasUsed?.ToJson(),
                from = From?.ToJson(),
                to = To?.ToJson(),
                contractAddress = ContractAddress?.ToJson(),
                logs = Logs?.Select(x => x.ToJson()).ToArray(),
                logsBloom = LogsBloom?.ToJson(),
                root = Root?.ToJson(),
                status = Status?.ToJson()
            };
        }
    }
}