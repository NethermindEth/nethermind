using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace Cortex.SimpleSerialize
{
    public class SszTree
    {
        private const int BITS_PER_BYTE = 8;
        private const int BYTES_PER_CHUNK = 32;
        private const int BYTES_PER_LENGTH_OFFSET = 4;

        private const int MAX_DEPTH = 32;
        private static readonly HashAlgorithm _hashAlgorithm = SHA256.Create();
        private static readonly byte[] _zeroHashes;

        static SszTree()
        {
            _zeroHashes = new byte[BYTES_PER_CHUNK * MAX_DEPTH];
            var hash = new byte[BYTES_PER_CHUNK];
            var buffer = new byte[BYTES_PER_CHUNK << 1];
            // Span accessors
            var hashes = _zeroHashes.AsSpan();
            var buffer1 = buffer.AsSpan(0, BYTES_PER_CHUNK);
            var buffer2 = buffer.AsSpan(BYTES_PER_CHUNK, BYTES_PER_CHUNK);
            // Fill
            for (var index = 1; index < MAX_DEPTH; index ++)
            {
                hash.CopyTo(buffer1);
                hash.CopyTo(buffer2);
                hash = _hashAlgorithm.ComputeHash(buffer);
                hash.CopyTo(hashes.Slice(index * BYTES_PER_CHUNK));
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
            if (element is SszLeafElement leaf)
            {
                var bytes = leaf.GetBytes();
                var packed = Pack(bytes);
                var paddedLength = leaf.IsVariableSize
                    ? leaf.ChunkCount * BYTES_PER_CHUNK
                    : packed.Length;
                var merkle = Merkleize(packed, paddedLength);
                if (leaf.IsVariableSize)
                {
                    return MixInLength(merkle, (uint)leaf.Length);
                }
                else
                {
                    return merkle;
                }
            }
            else if (element is SszCompositeElement composite)
            {
                //return Merkleize(composite.GetChildren()
                //    .SelectMany(x => HashTreeRootRecursive(x).ToArray())
                //    .ToArray());
                using (var memory = new MemoryStream())
                {
                    foreach (var child in composite.GetChildren())
                    {
                        memory.Write(HashTreeRootRecursive(child));
                    }
                    var bytes = memory.ToArray();
                    return Merkleize(bytes, bytes.Length);
                }
            }
            else
            {
                throw new NotImplementedException();
            }
        }

        private ReadOnlySpan<byte> MixInLength(ReadOnlySpan<byte> root, uint length)
        {
            var serializedLength = BitConverter.GetBytes(length);
            if (!BitConverter.IsLittleEndian)
            {
                Array.Reverse(serializedLength);
            }
            var mixed = new Span<byte>(new byte[BYTES_PER_CHUNK << 1]);
            root.CopyTo(mixed);
            serializedLength.CopyTo(mixed.Slice(BYTES_PER_CHUNK));
            return Hash(mixed.ToArray());
        }

        private ReadOnlySpan<byte> Merkleize(ReadOnlySpan<byte> chunks, int paddedLength)
        {
            if (paddedLength % BYTES_PER_CHUNK != 0)
            {
                throw new ArgumentOutOfRangeException("chunks.Length", chunks.Length, $"Chunks must by a multiple of {BYTES_PER_CHUNK} bytes");
            }
            if (paddedLength <= BYTES_PER_CHUNK)
            {
                return chunks;
            }
            var depth = 0;
            var width = BYTES_PER_CHUNK;
            while (width < paddedLength)
            {
                depth++;
                width <<= 1;
                if (depth > MAX_DEPTH)
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
            var hashWidth = ((data.Length / BYTES_PER_CHUNK + 1) >> 1) * BYTES_PER_CHUNK;
            var hashes = new Span<byte>(new byte[hashWidth]);
            for (var index = 0; index < hashWidth; index += BYTES_PER_CHUNK)
            {
                var dataIndex = index << 1;
                ReadOnlySpan<byte> hash;
                if (dataIndex + BYTES_PER_CHUNK >= data.Length)
                {
                    var padded = new Span<byte>(new byte[BYTES_PER_CHUNK << 1]);
                    data.Slice(dataIndex, BYTES_PER_CHUNK).CopyTo(padded);
                    _zeroHashes.AsSpan((depth - 1) * BYTES_PER_CHUNK, BYTES_PER_CHUNK).CopyTo(padded.Slice(BYTES_PER_CHUNK));
                    hash = Hash(padded);
                }
                else
                {
                    hash = Hash(data.Slice(dataIndex, BYTES_PER_CHUNK << 1));
                }
                hash.CopyTo(hashes.Slice(index, BYTES_PER_CHUNK));
            }
            return hashes;
        }

        private ReadOnlySpan<byte> Pack(ReadOnlySpan<byte> value)
        {
            var chunkCount = (value.Length + BYTES_PER_CHUNK - 1) / BYTES_PER_CHUNK;
            var chunks = new Span<byte>(new byte[chunkCount * BYTES_PER_CHUNK]);
            value.CopyTo(chunks);
            return chunks;
        }

        private SerializeResult SerializeRecursive(SszElement element)
        {
            if (element is SszLeafElement leaf)
            {
                return new SerializeResult(leaf.GetBytes(), leaf.IsVariableSize);
            }
            else if (element is SszCompositeElement composite)
            {
                var parts = composite.GetChildren().Select(x => SerializeRecursive(x));
                var isVariableSize = parts.Any(x => x.IsVariableSize);
                var offset = parts.Where(x => !x.IsVariableSize).Sum(x => x.Bytes.Length)
                    + parts.Count(x => x.IsVariableSize) * BYTES_PER_LENGTH_OFFSET;
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
                        index += BYTES_PER_LENGTH_OFFSET;
                        // Write variable parts at offset
                        part.Bytes.CopyTo(result.Slice(offset));
                        offset += part.Bytes.Length;
                    }
                }
                return new SerializeResult(result, isVariableSize);
            }
            else
            {
                throw new NotSupportedException();
            }
        }
    }

    internal class SerializeResult
    {
        public SerializeResult(ReadOnlySpan<byte> bytes, bool isVariableSize)
        {
            Bytes = bytes.ToArray();
            IsVariableSize = isVariableSize;
        }

        public byte[] Bytes { get; }

        public bool IsVariableSize { get; }
    }
}
