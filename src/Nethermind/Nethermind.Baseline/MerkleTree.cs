using System.Security.Cryptography;
using System;
using System.Runtime.CompilerServices;
using Nethermind.Serialization.Rlp;
using Nethermind.Trie;

[assembly: InternalsVisibleTo("Nethermind.Baseline.Test")]

namespace Nethermind.Baseline
{
    public class MerkleTree
    {
        private const int LeafLevel = 31;
        public const int TreeDepth = 32;
        private const uint MaxNodes = uint.MaxValue;
        private const uint MaxNodeIndex = MaxNodes - 1;
        private const uint FirstLeafIndexAsNodeIndex = MaxNodes / 2;
        public const uint MaxLeafIndex = uint.MaxValue / 2;

        private readonly IKeyValueStore _keyValueStore;

        public static readonly byte[][] s_zeroHashes = new byte[32][];
        private static readonly HashAlgorithm s_hashAlgorithm = SHA256.Create();

        private static byte[] _countKey;

        static MerkleTree()
        {
            _countKey = Rlp.Encode(uint.MaxValue).Bytes;
            s_zeroHashes[0] = new byte[32];
            for (int index = 1; index < 32; index++)
            {
                s_zeroHashes[index] = Hash(s_zeroHashes[index - 1], s_zeroHashes[index - 1]);
            }
        }

        public uint Count { get; set; }

        public MerkleTree(IKeyValueStore keyValueStore)
        {
            _keyValueStore = keyValueStore ?? throw new ArgumentNullException(nameof(keyValueStore));

            byte[] countBytes = _keyValueStore[_countKey];
            Count = countBytes == null ? 0 : new RlpStream(countBytes).DecodeUInt();
        }

        private void StoreCountInTheDb()
        {
            _keyValueStore[_countKey] = Rlp.Encode(Count).Bytes;
        }

        private Bytes32 LoadValue(uint level, uint indexAtLevel)
        {
            return LoadValue(GetNodeIndex(level, indexAtLevel));
        }

        private void SaveValue(uint nodeIndex, byte[] hashBytes)
        {
            _keyValueStore[Rlp.Encode(nodeIndex).Bytes] = hashBytes;
        }

        private void SaveValue(uint nodeIndex, Bytes32 hash)
        {
            SaveValue(nodeIndex, hash.AsSpan().ToArray());
        }

        internal static uint GetLevel(uint nodeIndex)
        {
            ValidateNodeIndex(nodeIndex);
            for (uint level = 0; level < LeafLevel; level++)
            {
                if (2ul << (int) level >= (ulong) nodeIndex + 2)
                {
                    return level;
                }
            }

            return 31;
        }

        private Bytes32 LoadValue(uint nodeIndex)
        {
            byte[] nodeHashBytes = _keyValueStore[Rlp.Encode(nodeIndex).Bytes];
            if (nodeHashBytes == null)
            {
                return Bytes32.Wrap(s_zeroHashes[31 - GetLevel(nodeIndex)]);
            }

            return Bytes32.Wrap(nodeHashBytes);
        }

        internal static uint GetIndexAtLevel(uint level, uint nodeIndex)
        {
            ValidateLevel(level);
            ValidateNodeIndex(nodeIndex);

            uint indexAtLevel = nodeIndex - ((1u << (int) level) - 1);
            ValidateIndexAtLevel(level, indexAtLevel);
            return indexAtLevel;
        }

        internal static uint GetLeafIndex(uint nodeIndex)
        {
            if (nodeIndex == uint.MaxValue ||
                nodeIndex < FirstLeafIndexAsNodeIndex)
            {
                throw new IndexOutOfRangeException($"Leaf indices start at {FirstLeafIndexAsNodeIndex}");
            }

            return nodeIndex - FirstLeafIndexAsNodeIndex;
        }

        internal static uint GetSiblingIndex(uint level, uint indexAtLevel)
        {
            ValidateLevel(level);
            ValidateIndexAtLevel(level, indexAtLevel);

            if (level == 0)
            {
                throw new IndexOutOfRangeException("Root node has no siblings.");
            }
            
            if (indexAtLevel % 2 == 0)
            {
                return indexAtLevel + 1;
            }

            return indexAtLevel - 1;
        }

        internal static void ValidateNodeIndex(uint nodeIndex)
        {
            if (nodeIndex > MaxNodeIndex)
            {
                throw new IndexOutOfRangeException($"Node index should be between 0 and {MaxNodeIndex}");
            }
        }

        internal static void ValidateLevel(uint level)
        {
            if (level > LeafLevel)
            {
                throw new IndexOutOfRangeException($"Tree level should be between 0 and {LeafLevel}");
            }
        }

        internal static void ValidateIndexAtLevel(uint level, uint indexAtLevel)
        {
            uint maxIndexAtLevel = (1u << (int) level) - 1u;
            if (indexAtLevel > maxIndexAtLevel)
            {
                throw new IndexOutOfRangeException($"Tree level {level} should only has indices between 0 and {maxIndexAtLevel}");
            }
        }

        internal static uint GetNodeIndex(uint level, uint indexAtLevel)
        {
            ValidateLevel(level);
            ValidateIndexAtLevel(level, indexAtLevel);

            return (1u << (int) level) - 1u + indexAtLevel;
        }

        internal static uint GetParentIndex(uint nodeIndex)
        {
            ValidateNodeIndex(nodeIndex);

            if (nodeIndex == 0)
            {
                throw new IndexOutOfRangeException("Root node has no parent");
            }

            return (nodeIndex + 1) / 2 - 1;
        }

        [MethodImpl(MethodImplOptions.Synchronized)]
        public void Insert(Bytes32 leaf)
        {
            _keyValueStore[Rlp.Encode(Count).Bytes] = leaf.AsSpan().ToArray();

            uint indexAtLevel = Count;
            uint siblingIndexAtLevel = GetSiblingIndex(LeafLevel, indexAtLevel);
            Bytes32 hash = leaf;
            Bytes32 siblingHash = LoadValue(LeafLevel, siblingIndexAtLevel);

            uint nodeIndex = GetNodeIndex(LeafLevel, Count);
            SaveValue(nodeIndex, hash);

            for (int level = LeafLevel; level > 0; level--)
            {
                uint parentIndex = GetParentIndex(GetNodeIndex((uint) level, indexAtLevel));
                var parentHash = Hash(hash.AsSpan(), siblingHash.AsSpan());
                SaveValue(parentIndex, parentHash);

                indexAtLevel = GetIndexAtLevel((uint) level - 1, parentIndex);

                if (level != 1)
                {
                    siblingIndexAtLevel = GetSiblingIndex((uint) level - 1, indexAtLevel);
                    hash = Bytes32.Wrap(parentHash);
                    siblingHash = LoadValue((uint) level - 1, siblingIndexAtLevel);
                }
            }

            Count++;
            StoreCountInTheDb();
        }

        public MerkleTreeNode[] GetProof(uint leafIndex)
        {
            ValidateIndexAtLevel(LeafLevel, leafIndex);

            MerkleTreeNode[] proof = new MerkleTreeNode[TreeDepth - 1];

            uint indexAtLevel = leafIndex;
            for (int proofLevel = 31; proofLevel > 0; proofLevel--)
            {
                uint siblingIndex = GetSiblingIndex((uint) proofLevel, indexAtLevel);
                uint siblingNodeIndex = GetNodeIndex((uint) proofLevel, siblingIndex);
                uint nodeIndex = GetNodeIndex((uint) proofLevel, indexAtLevel);
                proof[31 - proofLevel] = new MerkleTreeNode(LoadValue(siblingNodeIndex), siblingNodeIndex);
                indexAtLevel = GetIndexAtLevel((uint) proofLevel - 1u, GetParentIndex(nodeIndex));
            }

            return proof;
        }

        private static byte[] Hash(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
        {
            Span<byte> combined = new Span<byte>(new byte[64]);
            a.CopyTo(combined);
            b.CopyTo(combined.Slice(32));
            // try compute hash here?
            return s_hashAlgorithm.ComputeHash(combined.ToArray());
        }
    }
}