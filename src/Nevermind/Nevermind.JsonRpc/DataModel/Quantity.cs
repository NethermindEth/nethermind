using System;
using System.Numerics;

namespace Nevermind.JsonRpc.DataModel
{
    public class Quantity : IJsonRpcResult, IJsonRpcRequest
    {
        private const string Prefix = "0x";

        public BigInteger Value { get; set; }

        //TODO check conversion
        public string HexValue => Prefix + Value;

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
            //TODO check conversion - encoding
            Value = BigInteger.Parse(value.Substring(2));
        }
    }
}