using System;
using Nevermind.Core;
using Nevermind.Json;

namespace Nevermind.JsonRpc.DataModel
{
    public class Transaction : IJsonRpcRequest, IJsonRpcResult
    {
        private readonly IJsonSerializer _jsonSerializer;

        public Transaction()
        {
            _jsonSerializer = new JsonSerializer(new ConsoleLogger());
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
                blockHash = BlockHash.ToJson(),
                blockNumber = BlockNumber.ToJson(),
                transactionIndex = TransactionIndex.ToJson(),
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