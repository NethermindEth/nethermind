using System;

namespace Cortex.SimpleSerialize
{
    public class SszBasicElement : SszElement
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

        public override SszElementType ElementType => SszElementType.Basic;

        public ReadOnlySpan<byte> GetBytes() => _bytes;
    }
}
