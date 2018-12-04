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

using Nethermind.Core;

namespace Nethermind.JsonRpc.Data
{
    public class Transaction : IJsonRpcRequest, IJsonRpcResult
    {
        private readonly IJsonSerializer _jsonSerializer;

        public Transaction()
        {
            _jsonSerializer = new UnforgivingJsonSerializer();
        }

        public Transaction(IJsonSerializer jsonSerializer)
        {
            _jsonSerializer = jsonSerializer;
        }

        public Data Hash { get; set; }
        public Quantity Nonce { get; set; }
        public Data BlockHash { get; set; }
        public Quantity BlockNumber { get; set; }
        public Quantity TransactionIndex { get; set; }
        public Data From { get; set; }
        public Data To { get; set; }
        public Quantity Value { get; set; }
        public Quantity GasPrice { get; set; }
        public Quantity Gas { get; set; }
        public Data Data { get; set; }

        public void FromJson(string jsonValue)
        {
            var jsonObj = new { from = string.Empty, to = string.Empty, gas = string.Empty, gasPrice = string.Empty, value = string.Empty, data = string.Empty, nonce = string.Empty };
            var transaction = _jsonSerializer.DeserializeAnonymousType(jsonValue, jsonObj);
            From = !string.IsNullOrEmpty(transaction.from) ? new Data(transaction.from) : null;
            To = !string.IsNullOrEmpty(transaction.to) ? new Data(transaction.to) : null;
            Gas = !string.IsNullOrEmpty(transaction.gas) ? new Quantity(transaction.gas) : null;
            GasPrice = !string.IsNullOrEmpty(transaction.gasPrice) ? new Quantity(transaction.gasPrice) : null;
            Value = !string.IsNullOrEmpty(transaction.value) ? new Quantity(transaction.value) : null;
            Data = !string.IsNullOrEmpty(transaction.data) ? new Data(transaction.data) : null;
            Nonce = !string.IsNullOrEmpty(transaction.nonce) ? new Quantity(transaction.nonce) : null;
        }

        public object ToJson()
        {
            return new
            {
                hash = Hash.ToJson(),
                nonce = Nonce.ToJson(),
                blockHash = BlockHash?.ToJson(),
                blockNumber = BlockNumber?.ToJson(),
                transactionIndex = TransactionIndex?.ToJson(),
                from = From.ToJson(),
                to = To.ToJson(),
                value = Value.ToJson(),
                gasPrice = GasPrice.ToJson(),
                gas = Gas.ToJson(),
                input = Data.ToJson(),
            };
        }
    }
}