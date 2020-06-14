using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using Nethermind.Core2.Types;

[assembly: InternalsVisibleTo("Nethermind.Ssz.Test")]

namespace Nethermind.Ssz
{
    /// <summary>
    /// This will be moved to Eth2
    /// </summary>
    public abstract class MerkleTree
    {
        private const int LeafRow = 32;
        private const int LeafLevel = 0;
        public const int TreeHeight = 32;
        private const ulong FirstLeafIndexAsNodeIndex = MaxNodes / 2;
        private const ulong MaxNodes = (1ul << (TreeHeight + 1)) - 1ul;
        private const ulong MaxNodeIndex = MaxNodes - 1;

        private readonly IKeyValueStore<ulong, byte[]> _keyValueStore;

        private static ulong _countKey = ulong.MaxValue;

        static MerkleTree()
        {
        }

        /// <summary>
        /// Zero hashes will always be stored as 32 bytes (not truncated)
        /// </summary>
        protected abstract byte[][] ZeroHashesInternal { get; }

        public uint Count { get; set; }

        public MerkleTree(IKeyValueStore<ulong, byte[]> keyValueStore)
        {
            _keyValueStore = keyValueStore ?? throw new ArgumentNullException(nameof(keyValueStore));

            byte[]? countBytes = _keyValueStore[_countKey];
            Count = countBytes == null ? 0 : BinaryPrimitives.ReadUInt32LittleEndian(countBytes);
        }

        private void StoreCountInTheDb()
        {
            byte[] countBytes = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(countBytes, Count);
            _keyValueStore[_countKey] = countBytes;
        }

        private Bytes32 LoadValue(uint level, uint indexAtLevel)
        {
            return LoadValue(GetNodeIndex(level, indexAtLevel));
        }

        private void SaveValue(ulong nodeIndex, byte[] hashBytes)
        {
            _keyValueStore[nodeIndex] = hashBytes;
        }

        private void SaveValue(ulong nodeIndex, Bytes32 hash)
        {
            SaveValue(nodeIndex, hash.AsSpan().ToArray());
        }

        internal static uint GetRow(ulong nodeIndex)
        {
            ValidateNodeIndex(nodeIndex);
            for (uint row = 0; row < LeafRow; row++)
            {
                if (2ul << (int) row >= nodeIndex + 2)
                {
                    return row;
                }
            }

            return LeafRow;
        }

        private Bytes32 LoadValue(ulong nodeIndex)
        {
            byte[]? nodeHashBytes = _keyValueStore[nodeIndex];
            if (nodeHashBytes == null)
            {
                return Bytes32.Wrap(ZeroHashesInternal[LeafRow - GetRow(nodeIndex)]);
            }

            return Bytes32.Wrap(nodeHashBytes);
        }

        internal static uint GetIndexAtRow(uint row, ulong nodeIndex)
        {
            ValidateRow(row);
            ValidateNodeIndex(row, nodeIndex);

            uint indexAtRow = (uint)(nodeIndex - ((1ul << (int) row) - 1));
            ValidateIndexAtRow(row, indexAtRow);
            return indexAtRow;
        }

        internal static uint GetLeafIndex(ulong nodeIndex)
        {
            ValidateNodeIndex(LeafRow, nodeIndex);
            return (uint)(nodeIndex - FirstLeafIndexAsNodeIndex);
        }

        internal static uint GetSiblingIndex(uint row, uint indexAtRow)
        {
            ValidateRow(row);
            ValidateIndexAtRow(row, indexAtRow);

            if (row == 0)
            {
                throw new ArgumentOutOfRangeException("Root node has no siblings.");
            }
            
            return indexAtRow ^ 1;
        }

        internal static void ValidateNodeIndex(ulong nodeIndex)
        {
            if (nodeIndex > MaxNodeIndex)
            {
                throw new ArgumentOutOfRangeException($"Node index should be between 0 and {MaxNodeIndex}");
            }
        }
        
        private static ulong GetMinNodeIndex(uint row)
        {
            return (1ul << (int) row) - 1;
        }

        private static ulong GetMaxNodeIndex(uint row)
        {
            return (1ul << (int) (row + 1u)) - 2;
        }
        
        private static void ValidateNodeIndex(uint row, ulong nodeIndex)
        {
            ulong minNodeIndex = GetMinNodeIndex(row);
            ulong maxNodeIndex = GetMaxNodeIndex(row);

            if (nodeIndex < minNodeIndex || nodeIndex > maxNodeIndex)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(nodeIndex),
                    $"Node index at row {row} should be in the range of " +
                    $"[{minNodeIndex},{maxNodeIndex}] and was {nodeIndex}");
            }
        }

        internal static void ValidateRow(uint row)
        {
            if (row > LeafRow)
            {
                throw new ArgumentOutOfRangeException($"Tree level should be between 0 and {LeafRow}");
            }
        }

        internal static void ValidateIndexAtRow(uint row, uint indexAtRow)
        {
            uint maxIndexAtRow = (uint)((1ul << (int) row) - 1u);
            if (indexAtRow > maxIndexAtRow)
            {
                throw new ArgumentOutOfRangeException($"Tree level {row} should only have indices between 0 and {maxIndexAtRow}");
            }
        }

        internal static ulong GetNodeIndex(uint row, uint indexAtRow)
        {
            ValidateRow(row);
            ValidateIndexAtRow(row, indexAtRow);

            return (1ul << (int) row) - 1u + indexAtRow;
        }

        internal static ulong GetParentIndex(ulong nodeIndex)
        {
            ValidateNodeIndex(nodeIndex);

            if (nodeIndex == 0)
            {
                throw new ArgumentOutOfRangeException("Root node has no parent");
            }

            return (nodeIndex + 1) / 2 - 1;
        }

        [MethodImpl(MethodImplOptions.Synchronized)]
        public void Insert(Bytes32 leaf)
        {
            _keyValueStore[Count] = leaf.AsSpan().ToArray();

            uint indexAtRow = Count;
            uint siblingIndexAtRow = GetSiblingIndex(LeafRow, indexAtRow);
            Bytes32 hash = leaf;
            Bytes32 siblingHash = LoadValue(LeafRow, siblingIndexAtRow);

            ulong nodeIndex = GetNodeIndex(LeafRow, Count);
            SaveValue(nodeIndex, hash);

            for (int row = LeafRow; row > 0; row--)
            {
                ulong parentIndex = GetParentIndex(GetNodeIndex((uint) row, indexAtRow));
                var parentHash = Hash(hash.AsSpan(), siblingHash.AsSpan());
                SaveValue(parentIndex, parentHash);

                indexAtRow = GetIndexAtRow((uint) row - 1, parentIndex);

                if (row != 1)
                {
                    siblingIndexAtRow = GetSiblingIndex((uint) row - 1, indexAtRow);
                    hash = Bytes32.Wrap(parentHash);
                    
                    // we can quickly / efficiently find out that it will be a zero hash
                    siblingHash = LoadValue((uint) row - 1, siblingIndexAtRow);
                }
            }

            Count++;
            StoreCountInTheDb();
        }

        public MerkleTreeNode[] GetProof(uint leafIndex)
        {
            ValidateIndexAtRow(LeafRow, leafIndex);

            MerkleTreeNode[] proof = new MerkleTreeNode[TreeHeight];

            uint indexAtRow = leafIndex;
            for (int proofRow = LeafRow; proofRow > 0; proofRow--)
            {
                uint siblingIndex = GetSiblingIndex((uint) proofRow, indexAtRow);
                ulong siblingNodeIndex = GetNodeIndex((uint) proofRow, siblingIndex);
                ulong nodeIndex = GetNodeIndex((uint) proofRow, indexAtRow);
                proof[TreeHeight - proofRow] = new MerkleTreeNode(LoadValue(siblingNodeIndex), siblingNodeIndex);
                indexAtRow = GetIndexAtRow((uint) proofRow - 1u, GetParentIndex(nodeIndex));
            }

            return proof;
        }
        
        public MerkleTreeNode GetLeaf(uint leafIndex)
        {
            ulong nodeIndex = GetNodeIndex(LeafRow, leafIndex);
            Bytes32 value = LoadValue(LeafRow, leafIndex);
            return new MerkleTreeNode(Bytes32.Wrap(value.AsSpan().ToArray()), nodeIndex); 
        }
        
        public MerkleTreeNode[] GetLeaves(params uint[] leafIndexes)
        {
            MerkleTreeNode[] leaves = new MerkleTreeNode[leafIndexes.Length];
            for (int i = 0; i < leafIndexes.Length; i++)
            {
                leaves[i] = GetLeaf(leafIndexes[i]);
            }
             
            return leaves;
        }

        protected abstract byte[] Hash(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b);
    }
}