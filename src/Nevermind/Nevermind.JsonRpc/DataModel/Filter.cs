using System;
using System.Collections.Generic;

namespace Nevermind.JsonRpc.DataModel
{
    public class Filter : IJsonRpcRequest
    {
        public BlockParameter FromBlock { get; set; }
        public BlockParameter ToBlock { get; set; }
        public IEnumerable<Data> Address { get; set; }
        public IEnumerable<Data> Topics { get; set; }

        public void FromJson(string jsonValue)
        {
            throw new NotImplementedException();
        }
    }
}