using System;
using System.Collections;

namespace Cortex.SimpleSerialize
{
    public class SszBitvector : SszComposite
    {
        private readonly BitArray _value;

        public SszBitvector(BitArray bitArray)
        {
            _value = bitArray;
        }

        public override SszElementType ElementType => SszElementType.Bitvector;

        public ReadOnlySpan<byte> BitfieldBytes() => GetBytes();

        public ReadOnlySpan<byte> GetBytes()
        {
            var bytes = new byte[(_value.Length + 7) / 8];
            for (var index = 0; index < _value.Length; index++)
            {
                bytes[index / 8] |= (byte)((_value[index] ? 1 : 0) << (index % 8));
            }
            return bytes;
        }
    }
}
