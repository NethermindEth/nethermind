using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace Cortex.SimpleSerialize
{
    public class SszTree
    {
        // TODO: Split into serialization methods and merleize methods

        public const int BytesPerChunk = 32;
        //private const int BITS_PER_BYTE = 8;
        private const int BytesPerLengthOffset = 4;
        private const int MaxDepth = 63;
        private static readonly HashAlgorithm _hashAlgorithm = SHA256.Create();
        private static readonly byte[] _zeroHashes;

        static SszTree()
        {
            _zeroHashes = new byte[BytesPerChunk * MaxDepth];
            var buffer = new byte[(long)BytesPerChunk << 1];
            // Span accessors
            var hashes = _zeroHashes.AsSpan();
            var buffer1 = buffer.AsSpan(0, BytesPerChunk);
            var buffer2 = buffer.AsSpan(BytesPerChunk, BytesPerChunk);
            // Fill
            var hash = hashes.Slice(0, BytesPerChunk);
            for (var height = 1; height < MaxDepth; height++)
            {
                hash.CopyTo(buffer1);
                hash.CopyTo(buffer2);
                hash = hashes.Slice(height * BytesPerChunk, BytesPerChunk);
                var success = _hashAlgorithm.TryComputeHash(buffer, hash, out var bytesWritten);
                if (!success || bytesWritten != BytesPerChunk)
                {
                    throw new InvalidOperationException("Error generating zero hash values.");
                }
            }
        }

        public SszTree(SszElement rootElement)
        {
            RootElement = rootElement;
        }

        public SszElement RootElement { get; }

        public ReadOnlySpan<byte> HashTreeRoot()
        {
            var context = new Stack<int>(new[] { 0 });
            try
            {
                return HashTreeRootRecursive(RootElement, context);
            }
            catch (Exception ex)
            {
                var stack = string.Join(',', context);
                throw new Exception($"Error generating hash for tree at element [{stack}].", ex);
            }
        }

        public ReadOnlySpan<byte> Serialize()
        {
            return SerializeRecursive(RootElement).Bytes;
        }

        private ReadOnlySpan<byte> HashTreeRootRecursive(SszElement? element, Stack<int> context)
        {
            switch (element)
            {
                case null:
                    {
                        return new byte[BytesPerChunk];
                    }
                case SszBasicElement basic:
                    {
                        var bytes = basic.GetBytes();
                        var packed = Pack(bytes);
                        var paddedLength = (ulong)packed.Length;
                        return Merkleize(packed, paddedLength);
                    }
                case SszBasicVector vector:
                    {
                        var bytes = vector.GetBytes();
                        var packed = Pack(bytes);
                        var paddedLength = (ulong)packed.Length;
                        return Merkleize(packed, paddedLength);
                    }
                case SszBitvector vector:
                    {
                        var bytes = vector.BitfieldBytes();
                        var packed = Pack(bytes);
                        var paddedLength =
                            (((ulong)bytes.Length + BytesPerChunk - 1) / BytesPerChunk) * BytesPerChunk;
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
                case SszBitlist list:
                    {
                        var bytes = list.BitfieldBytes();
                        var packed = Pack(bytes);
                        var paddedLength =
                            ((list.ByteLimit + BytesPerChunk - 1) / BytesPerChunk) * BytesPerChunk;
                        var merkle = Merkleize(packed, paddedLength);
                        return MixInLength(merkle, (uint)list.Length);
                    }
                case SszVector vector:
                    {
                        byte[] bytes;
                        using (var memory = new MemoryStream())
                        {
                            var index = 0;
                            foreach (var value in vector.GetValues())
                            {
                                context.Push(index++);
                                memory.Write(HashTreeRootRecursive(value, context));
                                context.Pop();
                            }
                            bytes = memory.ToArray();
                        }
                        return Merkleize(bytes, (ulong)bytes.Length);
                    }
                case SszContainer container:
                    {
                        byte[] bytes;
                        using (var memory = new MemoryStream())
                        {
                            var index = 0;
                            foreach (var value in container.GetValues())
                            {
                                context.Push(index++);
                                memory.Write(HashTreeRootRecursive(value, context));
                                if (memory.Length % BytesPerChunk != 0)
                                {
                                    throw new Exception($"Chunks must by a multiple of {BytesPerChunk} bytes when adding to container.");
                                }
                                context.Pop();
                            }
                            bytes = memory.ToArray();
                        }
                        return Merkleize(bytes, (ulong)bytes.Length);
                    }
                case SszList list:
                    {
                        byte[] bytes;
                        using (var memory = new MemoryStream())
                        {
                            var index = 0;
                            foreach (var value in list.GetValues())
                            {
                                context.Push(index++);
                                memory.Write(HashTreeRootRecursive(value, context));
                                context.Pop();
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

        private ReadOnlySpan<byte> Merkleize(ReadOnlySpan<byte> chunks, ulong paddedLength)
        {
            if (paddedLength % BytesPerChunk != 0)
            {
                throw new ArgumentOutOfRangeException("chunks.Length", chunks.Length, $"Chunks must by a multiple of {BytesPerChunk} bytes.");
            }
            if ((ulong)chunks.Length > paddedLength)
            {
                throw new ArgumentOutOfRangeException("chunks.Length", chunks.Length, $"Chunks length exceeded padded length limit {paddedLength} bytes.");
            }

            if (paddedLength <= BytesPerChunk)
            {
                return chunks;
            }
            var depth = 0;
            var width = (ulong)BytesPerChunk;
            while (width < paddedLength)
            {
                depth++;
                width <<= 1;
                if (depth > MaxDepth)
                {
                    throw new ArgumentOutOfRangeException(nameof(depth), depth, "System data length limit exceeded");
                }
            }

            var startingHeight = depth;
            return MerkleizeRecursive(startingHeight, chunks);
        }

        private ReadOnlySpan<byte> MerkleizeRecursive(int height, ReadOnlySpan<byte> chunks)
        {
            if (height == 0)
            {
                return chunks;
            }

            var data = MerkleizeRecursive(height - 1, chunks);
            var hashWidth = ((data.Length / BytesPerChunk + 1) >> 1) * BytesPerChunk;
            var hashes = new Span<byte>(new byte[hashWidth]);
            for (var index = 0; index < hashWidth; index += BytesPerChunk)
            {
                var dataIndex = index << 1;
                ReadOnlySpan<byte> input;
                if (dataIndex + BytesPerChunk >= data.Length)
                {
                    var padded = new Span<byte>(new byte[BytesPerChunk << 1]);
                    data.Slice(dataIndex, BytesPerChunk).CopyTo(padded);
                    _zeroHashes.AsSpan((height - 1) * BytesPerChunk, BytesPerChunk).CopyTo(padded.Slice(BytesPerChunk));
                    input = padded;
                }
                else
                {
                    input = data.Slice(dataIndex, BytesPerChunk << 1);
                }
                var success = _hashAlgorithm.TryComputeHash(input, hashes.Slice(index, BytesPerChunk), out var bytesWritten);
                if (!success || bytesWritten != BytesPerChunk)
                {
                    throw new InvalidOperationException("Error generating hash value.");
                }
            }
            return hashes;
        }

        private ReadOnlySpan<byte> MixInLength(ReadOnlySpan<byte> root, ulong length)
        {
            var serializedLength = BitConverter.GetBytes(length);
            if (!BitConverter.IsLittleEndian)
            {
                Array.Reverse(serializedLength);
            }
            var mixed = new Span<byte>(new byte[BytesPerChunk << 1]);
            root.CopyTo(mixed);
            serializedLength.CopyTo(mixed.Slice(BytesPerChunk));
            var hash = new Span<byte>(new byte[BytesPerChunk]);
            var success = _hashAlgorithm.TryComputeHash(mixed, hash, out var bytesWritten);
            if (!success || bytesWritten != BytesPerChunk)
            {
                throw new InvalidOperationException("Error generating hash value.");
            }
            return hash;
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
                case null:
                    {
                        return new SerializeResult(Array.Empty<byte>(), isVariableSize: false);
                    }
                case SszBasicElement basic:
                    {
                        return new SerializeResult(basic.GetBytes(), isVariableSize: false);
                    }
                case SszBitvector vector:
                    {
                        return new SerializeResult(vector.GetBytes(), isVariableSize: false);
                    }
                case SszBitlist list:
                    {
                        return new SerializeResult(list.GetBytes(), isVariableSize: true);
                    }
                case SszBasicVector vector:
                    {
                        return new SerializeResult(vector.GetBytes(), isVariableSize: false);
                    }
                case SszBasicList list:
                    {
                        return new SerializeResult(list.GetBytes(), isVariableSize: true);
                    }
                case SszVector vector:
                    {
                        return SerializeCombineValues(vector.GetValues());
                    }
                case SszList list:
                    {
                        return SerializeCombineValues(list.GetValues());
                    }
                case SszContainer container:
                    {
                        return SerializeCombineValues(container.GetValues());
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
