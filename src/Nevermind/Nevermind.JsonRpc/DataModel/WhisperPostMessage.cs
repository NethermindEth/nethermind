using System;
using System.Collections.Generic;

namespace Nevermind.JsonRpc.DataModel
{
    public class WhisperPostMessage : IJsonRpcRequest
    {
        public Data From { get; set; }
        public Data To { get; set; }
        public IEnumerable<Data> Topics { get; set; }
        public Data Payload { get; set; }
        public Quantity Priority { get; set; }
        public Quantity Ttl { get; set; }

        public virtual void FromJson(string jsonValue)
        {
            throw new NotImplementedException();
        }
    }
}