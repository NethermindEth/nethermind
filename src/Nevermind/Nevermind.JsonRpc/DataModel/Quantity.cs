using System;
using System.Numerics;
using Nevermind.Core;
using Nevermind.Core.Extensions;

namespace Nevermind.JsonRpc.DataModel
{
    public class Quantity : IJsonRpcResult, IJsonRpcRequest
    {
        public Hex Value { get; private set; }

        public Quantity()
        {
        }

        public Quantity(BigInteger value)
        {
            var encodedValue = value.ToBigEndianByteArray();
            Value = encodedValue;
        }

        public Quantity(string value)
        {
            Value = value;
        }

        public BigInteger? GetValue()
        {
            return Value != null ? new BigInteger(Value) : (BigInteger?)null;
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