using System;
using System.Text;

namespace Nevermind.JsonRpc.DataModel
{
    public class Data : IJsonRpcResult, IJsonRpcRequest
    {
        private const string Prefix = "0x";

        public byte[] Value { get; set; }

        //TODO use Hex convertion
        public string HexValue => "0x" + Value;

        public object ToJson()
        {
            return HexValue;
        }

        public void FromJson(string jsonValue)
        {
            var value = jsonValue?.Trim() ?? string.Empty;
            if (!value.StartsWith(Prefix))
            {
                throw new Exception($"Incorrect parameter: {jsonValue ?? "null"}");
            }
            //TODO add Hex - encoding
            Value = Encoding.Default.GetBytes(value.Substring(2));
        }
    }
}