using System;
using System.Collections.Generic;

namespace Nevermind.JsonRpc.DataModel
{
    public class TransactionReceipt : IJsonRpcResult
    {
        public Data TransactionHash { get; set; }
        public Quantity TransactionIndex { get; set; }
        public Data BlockHash { get; set; }
        public Quantity BlockNumber { get; set; }
        public Quantity CumulativeGasUsed { get; set; }
        public Quantity GasUsed { get; set; }
        public Data ContractAddress { get; set; }
        public IEnumerable<Log> Logs { get; set; }
        public Data Root { get; set; }
        public Quantity Status { get; set; }

        public object ToJson()
        {
            throw new NotImplementedException();
        }
    }
}