using System;

namespace Nevermind.JsonRpc.DataModel
{
    public class Transaction : IJsonRpcRequest, IJsonRpcResult
    {
        public Data Hash { get; set; }
        public Data Nonce { get; set; }
        public Data BlockHash { get; set; }
        public Quantity BlockNumber { get; set; }
        public Quantity TransactionIndex { get; set; }
        public Data From { get; set; }
        public Data To { get; set; }
        public Data Value { get; set; }
        public Quantity GasPrice { get; set; }
        public Quantity Gas { get; set; }
        public Data Input { get; set; }

        public void FromJson(string jsonValue)
        {
            throw new NotImplementedException();
        }

        public object ToJson()
        {
            throw new NotImplementedException();
        }
    }
}