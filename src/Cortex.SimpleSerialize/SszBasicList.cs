using System;

namespace Cortex.SimpleSerialize
{
    public class SszBasicList : SszComposite
    {
        private readonly byte[] _bytes;

        public SszBasicList(ReadOnlySpan<byte> value, int limit)
        {
            Length = value.Length;
            ByteLimit = limit;
            _bytes = value.ToArray();
        }

        public SszBasicList(ReadOnlySpan<ushort> value, int limit)
        {
            var basicSize = sizeof(ushort);
            Length = value.Length;
            ByteLimit = limit * basicSize;
            _bytes = ToLittleEndianBytes(value, basicSize);
        }

        public SszBasicList(ReadOnlySpan<uint> value, int limit)
        {
            var basicSize = sizeof(uint);
            Length = value.Length;
            ByteLimit = limit * basicSize;
            _bytes = ToLittleEndianBytes(value, basicSize);
        }

        public SszBasicList(ReadOnlySpan<ulong> value, int limit)
        {
            var basicSize = sizeof(ulong);
            Length = value.Length;
            ByteLimit = limit * basicSize;
            _bytes = ToLittleEndianBytes(value, basicSize);
        }

        public int ByteLimit { get; }

        public override SszElementType ElementType => SszElementType.BasicList;

        public int Length { get; }

        public ReadOnlySpan<byte> GetBytes() => _bytes;
    }
}
