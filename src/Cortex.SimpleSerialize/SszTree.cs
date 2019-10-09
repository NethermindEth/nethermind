using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace Cortex.SimpleSerialize
{
    public class SszTree
    {
        public const int BytesPerChunk = 32;
        //private const int BITS_PER_BYTE = 8;
        private const int BytesPerLengthOffset = 4;
        private const int MaxDepth = 32;
        private static readonly HashAlgorithm _hashAlgorithm = SHA256.Create();
        private static readonly byte[] _zeroHashes;

        static SszTree()
        {
            _zeroHashes = new byte[BytesPerChunk * MaxDepth];
            var hash = new byte[BytesPerChunk];
            var buffer = new byte[BytesPerChunk << 1];
            // Span accessors
            var hashes = _zeroHashes.AsSpan();
            var buffer1 = buffer.AsSpan(0, BytesPerChunk);
            var buffer2 = buffer.AsSpan(BytesPerChunk, BytesPerChunk);
            // Fill
            for (var index = 1; index < MaxDepth; index++)
            {
                hash.CopyTo(buffer1);
                hash.CopyTo(buffer2);
                hash = _hashAlgorithm.ComputeHash(buffer);
                hash.CopyTo(hashes.Slice(index * BytesPerChunk));
            }
        }

        public SszTree(SszElement rootElement)
        {
            RootElement = rootElement;
        }

        public SszElement RootElement { get; }

        public ReadOnlySpan<byte> HashTreeRoot()
        {
            return HashTreeRootRecursive(RootElement);
        }

        public ReadOnlySpan<byte> Serialize()
        {
            return SerializeRecursive(RootElement).Bytes;
        }

        private ReadOnlySpan<byte> Hash(ReadOnlySpan<byte> data)
        {
            var hash = _hashAlgorithm.ComputeHash(data.ToArray());
            return hash;
        }

        private ReadOnlySpan<byte> HashTreeRootRecursive(SszElement element)
        {
            switch (element)
            {
                case SszBasicElement basic:
                    {
                        var bytes = basic.GetBytes();
                        var packed = Pack(bytes);
                        var paddedLength = packed.Length;
                        return Merkleize(packed, paddedLength);
                    }
                case SszBasicVector vector:
                    {
                        var bytes = vector.GetBytes();
                        var packed = Pack(bytes);
                        var paddedLength = packed.Length;
                        return Merkleize(packed, paddedLength);
                    }
                case SszBasicList list:
                    {
                        var bytes = list.GetBytes();
                        var packed = Pack(bytes);
                        var paddedLength =
                            ((list.ByteLimit + BytesPerChunk - 1) / BytesPerChunk) * BytesPerChunk;
                        var merkle = Merkleize(packed, paddedLength);
                        return MixInLength(merkle, (uint)list.Length);
                    }
                case SszContainer container:
                    {
                        //return Merkleize(composite.GetChildren()
                        //    .SelectMany(x => HashTreeRootRecursive(x).ToArray())
                        //    .ToArray());
                        byte[] bytes;
                        using (var memory = new MemoryStream())
                        {
                            foreach (var value in container.GetValues())
                            {
                                memory.Write(HashTreeRootRecursive(value));
                            }
                            bytes = memory.ToArray();
                        }
                        return Merkleize(bytes, bytes.Length);
                    }
                case SszList list:
                    {
                        byte[] bytes;
                        using (var memory = new MemoryStream())
                        {
                            foreach (var value in list.GetValues())
                            {
                                memory.Write(HashTreeRootRecursive(value));
                            }
                            bytes = memory.ToArray();
                        }
                        var paddedLength =
                            ((list.ByteLimit + BytesPerChunk - 1) / BytesPerChunk) * BytesPerChunk;
                        var merkle = Merkleize(bytes, paddedLength);
                        return MixInLength(merkle, (uint)list.Length);
                    }
                default:
                    {
                        throw new NotImplementedException();
                    }
            }
        }

        private ReadOnlySpan<byte> Merkleize(ReadOnlySpan<byte> chunks, int paddedLength)
        {
            if (paddedLength % BytesPerChunk != 0)
            {
                throw new ArgumentOutOfRangeException("chunks.Length", chunks.Length, $"Chunks must by a multiple of {BytesPerChunk} bytes");
            }
            if (paddedLength <= BytesPerChunk)
            {
                return chunks;
            }
            var depth = 0;
            var width = BytesPerChunk;
            while (width < paddedLength)
            {
                depth++;
                width <<= 1;
                if (depth > MaxDepth)
                {
                    throw new ArgumentOutOfRangeException(nameof(depth), depth, "System data length limit exceeded");
                }
            }
            if (chunks.Length > paddedLength)
            {
                throw new Exception("Input exceeds limit");
            }

            return MerkleizeRecursive(depth, chunks);
        }

        private ReadOnlySpan<byte> MerkleizeRecursive(int depth, ReadOnlySpan<byte> chunks)
        {
            if (depth == 0)
            {
                return chunks;
            }

            var data = MerkleizeRecursive(depth - 1, chunks);
            var hashWidth = ((data.Length / BytesPerChunk + 1) >> 1) * BytesPerChunk;
            var hashes = new Span<byte>(new byte[hashWidth]);
            for (var index = 0; index < hashWidth; index += BytesPerChunk)
            {
                var dataIndex = index << 1;
                ReadOnlySpan<byte> hash;
                if (dataIndex + BytesPerChunk >= data.Length)
                {
                    var padded = new Span<byte>(new byte[BytesPerChunk << 1]);
                    data.Slice(dataIndex, BytesPerChunk).CopyTo(padded);
                    _zeroHashes.AsSpan((depth - 1) * BytesPerChunk, BytesPerChunk).CopyTo(padded.Slice(BytesPerChunk));
                    hash = Hash(padded);
                }
                else
                {
                    hash = Hash(data.Slice(dataIndex, BytesPerChunk << 1));
                }
                hash.CopyTo(hashes.Slice(index, BytesPerChunk));
            }
            return hashes;
        }

        private ReadOnlySpan<byte> MixInLength(ReadOnlySpan<byte> root, uint length)
        {
            var serializedLength = BitConverter.GetBytes(length);
            if (!BitConverter.IsLittleEndian)
            {
                Array.Reverse(serializedLength);
            }
            var mixed = new Span<byte>(new byte[BytesPerChunk << 1]);
            root.CopyTo(mixed);
            serializedLength.CopyTo(mixed.Slice(BytesPerChunk));
            return Hash(mixed.ToArray());
        }

        private ReadOnlySpan<byte> Pack(ReadOnlySpan<byte> value)
        {
            var chunkCount = (value.Length + BytesPerChunk - 1) / BytesPerChunk;
            var chunks = new Span<byte>(new byte[chunkCount * BytesPerChunk]);
            value.CopyTo(chunks);
            return chunks;
        }

        private SerializeResult SerializeRecursive(SszElement element)
        {
            switch (element)
            {
                case SszBasicElement basic:
                    {
                        return new SerializeResult(basic.GetBytes(), isVariableSize: false);
                    }
                case SszBasicVector vector:
                    {
                        return new SerializeResult(vector.GetBytes(), isVariableSize: false);
                    }
                case SszBasicList list:
                    {
                        return new SerializeResult(list.GetBytes(), isVariableSize: true);
                    }
                case SszContainer container:
                    {
                        return SerializeCombineValues(container.GetValues());
                    }
                case SszList list:
                    {
                        return SerializeCombineValues(list.GetValues());
                    }
                default:
                    {
                        throw new NotSupportedException();
                    }
            }
        }

        private SerializeResult SerializeCombineValues(IEnumerable<SszElement> values)
        {
            var parts = values.Select(x => SerializeRecursive(x));
            var isVariableSize = parts.Any(x => x.IsVariableSize);
            var offset = parts.Where(x => !x.IsVariableSize).Sum(x => x.Bytes.Length)
                + parts.Count(x => x.IsVariableSize) * BytesPerLengthOffset;
            var length = offset + parts.Where(x => x.IsVariableSize).Sum(x => x.Bytes.Length);
            var result = new Span<byte>(new byte[length]);
            var index = 0;
            foreach (var part in parts)
            {
                if (!part.IsVariableSize)
                {
                    part.Bytes.CopyTo(result.Slice(index));
                    index += part.Bytes.Length;
                }
                else
                {
                    // Write offset in fixed part
                    var serializedOffset = BitConverter.GetBytes((uint)offset);
                    if (!BitConverter.IsLittleEndian)
                    {
                        Array.Reverse(serializedOffset);
                    }
                    serializedOffset.CopyTo(result.Slice(index));
                    index += BytesPerLengthOffset;
                    // Write variable parts at offset
                    part.Bytes.CopyTo(result.Slice(offset));
                    offset += part.Bytes.Length;
                }
            }
            return new SerializeResult(result, isVariableSize);
        }
    }
}
