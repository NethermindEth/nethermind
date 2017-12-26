using System;
using System.Collections.Generic;

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
        public IEnumerable<Data> Uncles { get; set; }

        public object ToJson()
        {
            return new {number = Number.ToJson()};
        }
    }
}