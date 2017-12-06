using System;

namespace Nevermind.JsonRpc.DataModel
{
    public class Transaction : IJsonRpcRequest, IJsonRpcResult
    {
        public Data From { get; set; }
        public Data To { get; set; }
        public Quantity Gas { get; set; }
        public Quantity GasPrice { get; set; }
        public Quantity Value { get; set; }
        public Data Data { get; set; }
        public Quantity Nonce { get; set; }

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