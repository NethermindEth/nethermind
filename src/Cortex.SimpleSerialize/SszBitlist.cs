using System;
using System.Collections;

namespace Cortex.SimpleSerialize
{
    public class SszBitlist : SszComposite
    {
        private readonly BitArray _value;

        public SszBitlist(BitArray bitArray, ulong limit)
        {
            // Chunk count for list of composite is N (we merkleize the hash root of each)
            ByteLimit = ((limit + 255) / 256) * SszTree.BytesPerChunk;
            _value = bitArray;
        }

        public SszBitlist(bool[] values, ulong limit)
            : this(new BitArray(values), limit)
        {
        }

        public ulong ByteLimit { get; }

        public override SszElementType ElementType => SszElementType.Bitlist;

        public int Length => _value.Length;

        public ReadOnlySpan<byte> BitfieldBytes()
        {
            var bytes = new byte[(_value.Length + 7) / 8];
            for (var index = 0; index < _value.Length; index++)
            {
                bytes[index / 8] |= (byte)((_value[index] ? 1 : 0) << (index % 8));
            }
            return bytes;
        }

        public ReadOnlySpan<byte> GetBytes()
        {
            var bytes = new byte[(_value.Length / 8) + 1];
            for (var index = 0; index < _value.Length; index++)
            {
                bytes[index / 8] |= (byte)((_value[index] ? 1 : 0) << (index % 8));
            }
            bytes[_value.Length / 8] |= (byte)(1 << (_value.Length % 8));
            return bytes;
        }
    }
}
