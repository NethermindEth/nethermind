using System;

namespace Cortex.SimpleSerialize
{
    public class SszLeafElement : SszElement
    {
        private readonly byte[] _bytes;
        private readonly SszElementType _elementType;

        public SszLeafElement(byte value)
        {
            _bytes = new byte[] { value };
            _elementType = SszElementType.Basic;
        }

        public SszLeafElement(ulong value)
        {
            var bytes = BitConverter.GetBytes(value);
            if (!BitConverter.IsLittleEndian)
            {
                Array.Reverse(bytes);
            }
            _bytes = bytes;
            _elementType = SszElementType.Basic;
        }

        public SszLeafElement(ushort value)
        {
            var bytes = BitConverter.GetBytes(value);
            if (!BitConverter.IsLittleEndian)
            {
                Array.Reverse(bytes);
            }
            _bytes = bytes;
            _elementType = SszElementType.Basic;
        }

        public SszLeafElement(uint value)
        {
            var bytes = BitConverter.GetBytes(value);
            if (!BitConverter.IsLittleEndian)
            {
                Array.Reverse(bytes);
            }
            _bytes = bytes;
            _elementType = SszElementType.Basic;
        }

        public SszLeafElement(byte[] value, bool isVariableSize = false)
        {
            _bytes = value;
            _elementType = isVariableSize ? SszElementType.BasicList : SszElementType.BasicVector;
            IsVariableSize = isVariableSize;
        }

        public override SszElementType ElementType { get { return _elementType; } }

        public bool IsVariableSize { get; }

        public ReadOnlySpan<byte> GetBytes() => _bytes;
    }
}
