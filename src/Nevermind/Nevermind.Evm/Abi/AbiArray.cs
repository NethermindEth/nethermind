using System;
using System.Numerics;
using Nevermind.Core.Extensions;

namespace Nevermind.Evm.Abi
{
    public class AbiArray : AbiType
    {
        private readonly AbiType _elementType;

        public AbiArray(AbiType elementType)
        {
            _elementType = elementType;
            CSharpType = _elementType.CSharpType.MakeArrayType();
        }

        public override bool IsDynamic => true;

        public override string Name => $"{_elementType}[]";

        public override Type CSharpType { get; }

        public override (object, int) Decode(byte[] data, int position)
        {
            BigInteger length;
            (length, position) = UInt.DecodeUInt(data, position);

            Array result = Array.CreateInstance(_elementType.CSharpType, (int)length);
            for (int i = 0; i < length; i++)
            {
                object element;
                (element, position) = _elementType.Decode(data, position);

                result.SetValue(element, i);
            }

            return (result, position);
        }

        public override byte[] Encode(object arg)
        {
            if (arg is Array input)
            {
                byte[][] encodedItems = new byte[input.Length + 1][];
                int i = 0;
                encodedItems[i++] = UInt.Encode((BigInteger)input.Length);
                foreach (object o in input)
                {
                    encodedItems[i++] = _elementType.Encode(o);
                }

                return Bytes.Concat(encodedItems);
            }

            throw new AbiException(AbiEncodingExceptionMessage);
        }
    }
}