using System;

namespace Cortex.SimpleSerialize
{
    public class SszLeafElement : SszElement
    {
        private const int BYTES_PER_CHUNK = 32;

        private readonly byte[] _bytes;
        private readonly SszElementType _elementType;

        public SszLeafElement(ulong value)
        {
            var bytes = BitConverter.GetBytes(value);
            if (!BitConverter.IsLittleEndian)
            {
                Array.Reverse(bytes);
            }
            _bytes = bytes;
            _elementType = SszElementType.Basic;
            ChunkCount = 1;
        }

        public SszLeafElement(byte value)
        {
            _bytes = new byte[] { value };
            _elementType = SszElementType.Basic;
            ChunkCount = 1;
        }

        public SszLeafElement(ushort value)
        {
            _bytes = Serialize(value);
            _elementType = SszElementType.Basic;
            ChunkCount = 1;
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
            ChunkCount = 1;
        }

        public SszLeafElement(byte[] value, bool isVariableSize = false)
        {
            _bytes = value;
            _elementType = isVariableSize ? SszElementType.BasicList : SszElementType.BasicVector;
            IsVariableSize = isVariableSize;
            ChunkCount = (value.Length + BYTES_PER_CHUNK - 1) / BYTES_PER_CHUNK;
        }

        public SszLeafElement(ushort[] value, bool isVariableSize = false, int limit = -1)
        {
            _bytes = new byte[value.Length << 1];
            for (var index = 0; index < value.Length; index++)
            {
                var bytes = Serialize(value[index]);
                bytes.CopyTo(_bytes, index << 1);
            }
            _elementType = isVariableSize ? SszElementType.BasicList : SszElementType.BasicVector;
            IsVariableSize = isVariableSize;
            Length = value.Length;
            ChunkCount = (limit * 2 + BYTES_PER_CHUNK - 1) / BYTES_PER_CHUNK;
        }

        public SszLeafElement(uint[] value, bool isVariableSize = false, int limit = -1)
        {
            _bytes = new byte[value.Length << 2];
            for (var index = 0; index < value.Length; index++)
            {
                var bytes = Serialize(value[index]);
                bytes.CopyTo(_bytes, index << 2);
            }
            _elementType = isVariableSize ? SszElementType.BasicList : SszElementType.BasicVector;
            IsVariableSize = isVariableSize;
            Length = value.Length;
            ChunkCount = (limit * 4 + BYTES_PER_CHUNK - 1) / BYTES_PER_CHUNK;
        }

        public SszLeafElement(ulong[] value, bool isVariableSize = false)
        {
            _bytes = new byte[value.Length << 3];
            for (var index = 0; index < value.Length; index++)
            {
                var bytes = Serialize(value[index]);
                bytes.CopyTo(_bytes, index << 3);
            }
            _elementType = isVariableSize ? SszElementType.BasicList : SszElementType.BasicVector;
            IsVariableSize = isVariableSize;
            ChunkCount = (value.Length * 8 + BYTES_PER_CHUNK - 1) / BYTES_PER_CHUNK;
        }

        public override SszElementType ElementType { get { return _elementType; } }

        public bool IsVariableSize { get; }

        public ReadOnlySpan<byte> GetBytes() => _bytes;

        public int ChunkCount { get; }

        public int Length { get; }

        private static byte[] Serialize(ushort value)
        {
            var bytes = BitConverter.GetBytes(value);
            if (!BitConverter.IsLittleEndian)
            {
                Array.Reverse(bytes);
            }

            return bytes;
        }

        private static byte[] Serialize(uint value)
        {
            var bytes = BitConverter.GetBytes(value);
            if (!BitConverter.IsLittleEndian)
            {
                Array.Reverse(bytes);
            }

            return bytes;
        }

        private static byte[] Serialize(ulong value)
        {
            var bytes = BitConverter.GetBytes(value);
            if (!BitConverter.IsLittleEndian)
            {
                Array.Reverse(bytes);
            }

            return bytes;
        }
    }
}
