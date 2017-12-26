using System;

namespace Nevermind.JsonRpc.DataModel
{
    public class WhisperMessage : WhisperPostMessage, IJsonRpcResult
    {
        public Data Hash { get; set; }
        public Quantity Expiry { get; set; }
        public Quantity Sent { get; set; }
        public Data WorkProved { get; set; }

        public object ToJson()
        {
            throw new NotImplementedException();
        }
    }
}