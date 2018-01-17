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

using System;
using System.Collections.Generic;
using System.Linq;

namespace Nevermind.JsonRpc.DataModel
{
    public class Block : IJsonRpcResult
    {
        public Quantity Number { get; set; }
        public Data Hash { get; set; }
        public Data ParentHash { get; set; }
        public Data Nonce { get; set; }
        public Data Sha3Uncles { get; set; }
        public Data LogsBloom { get; set; }
        public Data TransactionsRoot { get; set; }
        public Data StateRoot { get; set; }
        public Data ReceiptsRoot { get; set; }
        public Data Miner { get; set; }
        public Quantity Difficulty { get; set; }
        public Quantity TotalDifficulty { get; set; }
        public Data ExtraData { get; set; }
        public Quantity Size { get; set; }
        public Quantity GasLimit { get; set; }
        public Quantity GasUsed { get; set; }
        public Quantity Timestamp { get; set; }
        public IEnumerable<Transaction> Transactions { get; set; }
        public IEnumerable<Data> TransactionHashes { get; set; }
        public IEnumerable<Data> Uncles { get; set; }

        public object ToJson()
        {
            return new
            {
                number = Number?.ToJson(),
                hash = Hash?.ToJson(),
                parentHash = ParentHash?.ToJson(),
                nonce = Nonce?.ToJson(),
                sha3Uncles = Sha3Uncles?.ToJson(),
                logsBloom = LogsBloom?.ToJson(),
                transactionsRoot = TransactionsRoot?.ToJson(),
                stateRoot = StateRoot?.ToJson(),
                receiptsRoot = ReceiptsRoot?.ToJson(),
                miner = Miner?.ToJson(),
                difficulty = Difficulty?.ToJson(),
                totalDifficulty = TotalDifficulty?.ToJson(),
                extraData = ExtraData?.ToJson(),
                size = Size?.ToJson(),
                gasLimit = GasLimit?.ToJson(),
                gasUsed = GasUsed?.ToJson(),
                timestamp = Timestamp?.ToJson(),
                transactions = Transactions?.Select(x => x.ToJson()).ToArray() ?? TransactionHashes?.Select(x => x.ToJson()).ToArray(),
                uncles = Uncles?.Select(x => x.ToJson()).ToArray()
            };
        }
    }
}