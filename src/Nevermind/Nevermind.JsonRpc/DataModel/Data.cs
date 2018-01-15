using System.Text;
using Nevermind.Core;

namespace Nevermind.JsonRpc.DataModel
{
    public class Data : IJsonRpcResult, IJsonRpcRequest
    {
        public Hex Value { get; private set; }

        public Data()
        {
        }

        public Data(string value)
        {
            Value = value;
        }

        public Data(byte[] value)
        {
            Value = value;
        }

        public Data(Hex value)
        {
            Value = value;
        }

        public object ToJson()
        {
            return Value?.ToString(true, true);
        }

        public void FromJson(string jsonValue)
        {
            Value = jsonValue;
        }
    }
}