using System;

namespace Cortex.SimpleSerialize
{
    public class SszBasicElement : SszLeafElement
    {
        private readonly byte[] _bytes;

        public SszBasicElement(ulong value)
        {
            _bytes = ToLittleEndianBytes<ulong>(new ulong[] { value }, sizeof(ulong));
            //var bytes = BitConverter.GetBytes(value);
            //if (!BitConverter.IsLittleEndian)
            //{
            //    Array.Reverse(bytes);
            //}
            //_bytes = bytes;
        }

        public SszBasicElement(byte value)
        {
            _bytes = new byte[] { value };
        }

        public SszBasicElement(ushort value)
        {
            var bytes = BitConverter.GetBytes(value);
            if (!BitConverter.IsLittleEndian)
            {
                Array.Reverse(bytes);
            }
            _bytes = bytes;
        }

        public SszBasicElement(uint value)
        {
            var bytes = BitConverter.GetBytes(value);
            if (!BitConverter.IsLittleEndian)
            {
                Array.Reverse(bytes);
            }
            _bytes = bytes;
        }

        public override SszElementType ElementType { get { return SszElementType.Basic; } }

        public override ReadOnlySpan<byte> GetBytes() => _bytes;
    }
}
