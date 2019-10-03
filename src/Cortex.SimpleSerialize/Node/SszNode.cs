using System;
using System.Security.Cryptography;

namespace Cortex.SimpleSerialize
{
    public abstract class SszNode
    {
        private const int BYTES_PER_CHUNK = 32;
        private const int MAX_DEPTH = 25;
        private static readonly HashAlgorithm _hashAlgorithm = SHA256.Create();
        private static readonly byte[] _hashZeros = _hashAlgorithm.ComputeHash(new byte[BYTES_PER_CHUNK << 1]);


        // SszNodeType
        // leaf nodes (e.g. basic types/vectors of basic types)
        //  => have GetLeafBytes(), also length?, and IsVariableSize
        // other nodes (containers, lists)
        //  => have IEnumerable<node> of children
        // 3x visitors, for Merkleization, Serialization, and both (self-signed)

        // X - A
        //   - B - C
        //         D

        // X_summary - A
        //             B_root

        // hash(X) and hash(X_summary) are the same
        // because B_root = hash(B) = hash(C,D)
        // (B is a class, B_root is a hash)

        // ulong (Eth1Data)
        // byte array
        // class
        // child class
        // child list

        // a) hash (to store, summarize, check or validate)
        // b) serialize and hash to sign
        // c) just serialize

        public abstract bool IsVariableSize { get; }

        public abstract ReadOnlySpan<byte> HashTreeRoot();

        public abstract ReadOnlySpan<byte> Serialize();

        protected ReadOnlySpan<byte> Merkleize(ReadOnlySpan<byte> chunks)
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

        protected ReadOnlySpan<byte> Pack(ReadOnlySpan<byte> value)
        {
            var chunkCount = (value.Length + BYTES_PER_CHUNK - 1) / BYTES_PER_CHUNK;
            var chunks = new Span<byte>(new byte[chunkCount * BYTES_PER_CHUNK]);
            value.CopyTo(chunks);
            return chunks;
        }

        private ReadOnlySpan<byte> Hash(ReadOnlySpan<byte> data)
        {
            var hash = _hashAlgorithm.ComputeHash(data.ToArray());
            return hash;
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
    }
}
