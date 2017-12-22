using System;

namespace Nevermind.JsonRpc.DataModel
{
    public class Block : IJsonRpcResult
    {
        public Quantity Number { get; set; }

        public object ToJson()
        {
            return new {number = Number.ToJson()};
        }
    }
}