using System;

namespace Cortex.SimpleSerialize
{
    public class SszBasicVector : SszComposite
    {
        private readonly byte[] _bytes;

        public SszBasicVector(ReadOnlySpan<byte> value)
        {
            _bytes = value.ToArray();
        }

        public SszBasicVector(ReadOnlySpan<ushort> value)
        {
            _bytes = ToLittleEndianBytes(value, sizeof(ushort));
        }

        public SszBasicVector(ReadOnlySpan<uint> value)
        {
            _bytes = ToLittleEndianBytes(value, sizeof(uint));
        }

        public SszBasicVector(ReadOnlySpan<ulong> value)
        {
            _bytes = ToLittleEndianBytes(value, sizeof(ulong));
        }

        public override SszElementType ElementType => SszElementType.BasicVector;

        public ReadOnlySpan<byte> GetBytes() => _bytes;
    }
}
