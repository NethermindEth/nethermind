using System;
using System.IO;
using System.Security.Cryptography;

namespace Cortex.SimpleSerialize
{
    public class SszTree
    {
        private const int BYTES_PER_CHUNK = 32;
        private const int MAX_DEPTH = 25;
        private static readonly HashAlgorithm _hashAlgorithm = SHA256.Create();
        private static readonly byte[] _hashZeros = _hashAlgorithm.ComputeHash(new byte[BYTES_PER_CHUNK << 1]);

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
            return SerializeRecursive(RootElement);
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
                return Merkleize(Pack(leaf.GetBytes()));
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
                    return Merkleize(memory.ToArray());
                }
            }
            else
            {
                throw new NotImplementedException();
            }
        }

        private ReadOnlySpan<byte> Merkleize(ReadOnlySpan<byte> chunks)
        {
            if (chunks.Length % BYTES_PER_CHUNK != 0)
            {
                throw new ArgumentOutOfRangeException("chunks.Length", chunks.Length, $"Chunks must by a multiple of {BYTES_PER_CHUNK} bytes");
            }
            if (chunks.Length <= BYTES_PER_CHUNK)
            {
                return chunks;
            }
            return MerkleizeRecursive(0, chunks);
        }

        private ReadOnlySpan<byte> MerkleizeRecursive(int depth, ReadOnlySpan<byte> chunks)
        {
            if (depth > MAX_DEPTH)
            {
                throw new ArgumentOutOfRangeException(nameof(depth), depth, "System data length limit exceeded");
            }

            var width = BYTES_PER_CHUNK << depth;
            if (chunks.Length <= width)
            {
                return chunks;
            }

            var data = MerkleizeRecursive(depth + 1, chunks);

            var hashes = new Span<byte>(new byte[width]);
            for (var index = 0; index < width; index += BYTES_PER_CHUNK)
            {
                var dataIndex = index << 1;
                ReadOnlySpan<byte> hash;
                if (dataIndex >= data.Length)
                {
                    hash = _hashZeros;
                }
                else if (dataIndex + BYTES_PER_CHUNK >= data.Length)
                {
                    var padded = new Span<byte>(new byte[BYTES_PER_CHUNK << 1]);
                    data.Slice(dataIndex, BYTES_PER_CHUNK).CopyTo(padded);
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

        private ReadOnlySpan<byte> SerializeRecursive(SszElement element)
        {
            if (element is SszLeafElement leaf)
            {
                return leaf.GetBytes();
            }
            else if (element is SszCompositeElement composite)
            {
                // TODO: Not sure which is fastest; maybe a Span/buffer based solution?

                //return composite.GetChildren()
                //    .SelectMany(x => SerializeRecursive(x).ToArray())
                //    .ToArray();
                using (var memory = new MemoryStream())
                {
                    foreach (var child in composite.GetChildren())
                    {
                        memory.Write(SerializeRecursive(child));
                    }
                    return memory.ToArray();
                }
            }
            else
            {
                throw new NotSupportedException();
            }
        }
    }
}
