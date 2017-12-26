using System;
using System.Collections.Generic;

namespace Nevermind.JsonRpc.DataModel
{
    public class Log : IJsonRpcResult
    {
        public bool Removed { get; set; }
        public Quantity LogIndex { get; set; }
        public Quantity TransactionIndex { get; set; }
        public Data TransactionHash { get; set; }
        public Data BlockHash { get; set; }
        public Quantity BlockNumber { get; set; }
        public Data Address { get; set; }
        public Data Data { get; set; }
        public IEnumerable<Data> Topics { get; set; }

        public object ToJson()
        {
            throw new NotImplementedException();
        }
    }
}