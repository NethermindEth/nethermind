using System;
using System.Collections.Generic;

namespace Nevermind.JsonRpc.DataModel
{
    public class WhisperFilter : IJsonRpcRequest
    {
        public Data To { get; set; }
        public IEnumerable<Data> Topics { get; set; }

        public void FromJson(string jsonValue)
        {
            throw new NotImplementedException();
        }
    }
}