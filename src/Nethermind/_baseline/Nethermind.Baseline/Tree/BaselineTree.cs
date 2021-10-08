//  Copyright (c) 2018 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Linq;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Repositories;
using Nethermind.Trie;

[assembly: InternalsVisibleTo("Nethermind.Baseline.Test")]

namespace Nethermind.Baseline.Tree
{
    public abstract partial class BaselineTree
    {
        public int TruncationLength { get; }
        public const int LeafRow = 32;
        private const int LeafLevel = 0;
        public const int TreeHeight = 32;
        private const ulong FirstLeafIndexAsNodeIndex = MaxNodes / 2;
        private const ulong MaxNodes = (1ul << (TreeHeight + 1)) - 1ul;
        private const ulong MaxNodeIndex = MaxNodes - 1;
        public const uint MaxLeafIndex = uint.MaxValue;

        private readonly IDb _db;
        private readonly IKeyValueStore _metadataKeyValueStore;
        private readonly byte[] _dbPrefix;
        private readonly object _synchroObject = new object();

        static BaselineTree()
        {
        }

        public uint Count { get; private set; }

        public long LastBlockWithLeaves { get; private set; }

        public void MemorizePastCount(long blockNumber, uint count)
        { 
            if(_logger.IsWarn) _logger.Warn($"Saving block number count ({count}, {LastBlockWithLeaves}) of {this} in block {blockNumber}");
            Metadata.SaveBlockNumberCount(blockNumber, count, LastBlockWithLeaves);
        }
        
        public void MemorizeCurrentCount(Keccak blockHash, long blockNumber, uint count)
        { 
            if(_logger.IsWarn) _logger.Warn($"Saving block number count ({count}, {LastBlockWithLeaves}) of {this} in block {blockNumber}");
            Metadata.SaveBlockNumberCount(blockNumber, count, LastBlockWithLeaves);
            Metadata.SaveCurrentBlockInDb(blockHash, blockNumber);
            LastBlockWithLeaves = blockNumber;
            LastBlockDbHash = blockHash;
        }

        public BaselineTreeMetadata Metadata { get; private set; }

        public Keccak LastBlockDbHash { get; set; } = Keccak.Zero;

        /* baseline does not use a sparse merkle tree - instead they use a single zero hash value
           does it expose any attack vectors? */
        internal static Keccak ZeroHash = Keccak.Zero;

        private ILogger _logger;
        
        public BaselineTree(IDb db, IKeyValueStore metadataKeyValueStore, byte[] _dbPrefix, int truncationLength, ILogger logger)
        {
            _logger = logger;
            TruncationLength = truncationLength;
            _db = db ?? throw new ArgumentNullException(nameof(db));
            _metadataKeyValueStore = metadataKeyValueStore ?? throw new ArgumentNullException(nameof(metadataKeyValueStore));
            this._dbPrefix = _dbPrefix;
            Metadata = new BaselineTreeMetadata(metadataKeyValueStore, _dbPrefix, _logger);

            InitializeMetadata();
            Root = Count == 0 ? Keccak.Zero : LoadValue(new Index(0, 0));
        }

        private byte[] BuildDbKey(ulong nodeIndex)
        {
            return Rlp.Encode(Rlp.Encode(_dbPrefix), Rlp.Encode(nodeIndex)).Bytes;
        }

        private void SaveValue(in Index index, byte[] hashBytes)
        {
            _db[BuildDbKey(index.NodeIndex)] = hashBytes;
        }

        private Keccak LoadValue(in Index index)
        {
            byte[]? nodeHashBytes = _db[BuildDbKey(index.NodeIndex)];
            if (nodeHashBytes == null)
            {
                return ZeroHash;
            }

            return new Keccak(nodeHashBytes);
        }

        public BatchWrite StartBatch()
        {
            return new BatchWrite(_synchroObject);
        }

        public uint GetBlockCount(long blockNumber)
        {
            return Metadata.GetBlockCount(LastBlockWithLeaves, blockNumber);
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="blockNumber"></param>
        /// <returns>Deleted leaves count</returns>
        public uint GoBackTo(long blockNumber)
        {
            (uint count, long newLastBlockWithLeaves) = Metadata.GoBackTo(blockNumber, LastBlockWithLeaves);
            var leavesToDelete = Count - count;
            LastBlockWithLeaves = newLastBlockWithLeaves;
            Delete(leavesToDelete, false);
            return leavesToDelete;
        }

        private void InitializeMetadata()
        {
            (Keccak LastBlockDbHash, long LastBlockWithLeaves) currentBlock = Metadata.LoadCurrentBlockInDb();
            LastBlockDbHash = currentBlock.LastBlockDbHash;
            LastBlockWithLeaves = currentBlock.LastBlockWithLeaves;
            Count = LoadCount();
            
            if(_logger.IsInfo) _logger.Info(
                $"Initialized tree {_dbPrefix.ToHexString()} with count {Count}, last block hash {LastBlockDbHash} and last block with leaves {LastBlockWithLeaves}.");
        }

        private uint LoadCount()
        {
            // this is an incorrect binary search approach
            // that will fail if any of the leaves are zero hashes
            // we should read count from the corresponding contract

            ulong left = GetMinNodeIndex(LeafRow);
            ulong right = GetMaxNodeIndex(LeafRow);
            ulong? topIndex = Binary.Search(left, right, ni => !ZeroHash.Equals(LoadValue(new Index(ni))));
            if (!topIndex.HasValue)
            {
                return 0;
            }

            return new Index(topIndex.Value).IndexAtRow + 1;
        }

        private static ulong GetMinNodeIndex(in uint row)
        {
            return (1ul << (int)row) - 1;
        }

        private static ulong GetMaxNodeIndex(in uint row)
        {
            return (1ul << (int)(row + 1u)) - 2;
        }

        [MethodImpl(MethodImplOptions.Synchronized)]
        public void Insert(Keccak leaf, bool recalculateHashes = true)
        {
            Index index = new Index(LeafRow, Count);
            Modify(index, leaf, recalculateHashes);
            Count++;
        }

        public void DeleteLast(bool recalculateHashes = true)
        {
            Index index = new Index(LeafRow, Count - 1);
            Modify(index, ZeroHash, recalculateHashes);
            Count--;
        }

        public void Delete(uint leavesToRemove, bool recalculateHashes = true)
        {
            for (uint i = 0; i < leavesToRemove; ++i)
            {
                Index index = new Index(LeafRow, Count - i - 1);
                Modify(index, ZeroHash, recalculateHashes);
            }

            Count = Count - leavesToRemove;
        }

        private void Modify(Index index, Keccak leaf, bool recalculateHashes = true)
        {
            byte[] hash = leaf.Bytes;
            SaveValue(index, hash);
            if (recalculateHashes)
            {
                Index siblingIndex = index.Sibling();
                Keccak siblingHash = LoadValue(siblingIndex);
                for (int row = LeafRow; row > 0; row--)
                {
                    byte[] parentHash = new byte[32];
                    if (index.IsLeftSibling())
                    {
                        Hash(hash.AsSpan(), siblingHash.Bytes.AsSpan(), parentHash);
                    }
                    else
                    {
                        Hash(siblingHash.Bytes.AsSpan(), hash.AsSpan(), parentHash);
                    }
                    Index parentIndex = index.Parent();
                    SaveValue(parentIndex, parentHash);
                    index = parentIndex;
                    if (row != 1)
                    {
                        siblingIndex = index.Sibling();
                        hash = parentHash;
                        // we can quickly / efficiently find out that it will be a zero hash
                        siblingHash = LoadValue(siblingIndex);
                    }
                    else
                    {
                        Root = new Keccak(parentHash);
                    }
                }
            }
        }

        public bool Verify(Keccak root, Keccak leaf, BaselineTreeNode[] siblingPath)
        {
            Span<byte> value = stackalloc byte[32];
            leaf.Bytes.AsSpan().CopyTo(value);
            for (int testDepth = 0; testDepth < TreeHeight; testDepth++)
            {
                BaselineTreeNode branchValue = siblingPath[testDepth];
                Index index = new Index(branchValue.NodeIndex);
                if (index.IsLeftSibling())
                {
                    // Console.WriteLine($"Verify {branchValue}+{value.ToArray()} at level {testDepth} =>");
                    Hash(branchValue.Hash.Bytes.AsSpan(), value, value);
                    // Console.WriteLine($"  {value.ToHexString()}");
                }
                else
                {
                    // Console.WriteLine($"Verify {value.ToHexString()}+{branchValue.Hash} at level {testDepth} =>");
                    Hash(value, branchValue.Hash.Bytes.AsSpan(), value);
                    // Console.WriteLine($"  {value.ToHexString()}");
                }
            }

            return value.SequenceEqual(root.Bytes.AsSpan());
        }

        public BaselineTreeNode[] GetProof(in uint leafIndex)
        {
            Index index = new Index(LeafRow, leafIndex);
            BaselineTreeNode[] proof = new BaselineTreeNode[TreeHeight];

            int i = 0;
            for (int proofRow = LeafRow; proofRow > 0; proofRow--)
            {
                Index siblingIndex = index.Sibling();
                proof[i++] = new BaselineTreeNode(LoadValue(siblingIndex), siblingIndex.NodeIndex);
                index = index.Parent();
            }

            return proof;
        }

        public BaselineTreeNode GetLeaf(in uint leafIndex)
        {
            Index index = new Index(LeafRow, leafIndex);
            Keccak value = LoadValue(index);
            return new BaselineTreeNode(value, index.NodeIndex);
        }

        public BaselineTreeNode[] GetLeaves(params uint[] leafIndexes)
        {
            BaselineTreeNode[] leaves = new BaselineTreeNode[leafIndexes.Length];
            for (int i = 0; i < leafIndexes.Length; i++)
            {
                leaves[i] = GetLeaf(leafIndexes[i]);
            }

            return leaves;
        }

        public void CalculateHashes(uint startIndex = 0, uint? endIndex = null)
        {
            var nodesToIterate = endIndex == null ? Count : endIndex.Value;
            uint startingRowIndex = startIndex - startIndex % 2;
            for (uint row = LeafRow; row > 0; row--)
            {
                for (uint indexAtRow = startingRowIndex; indexAtRow < nodesToIterate; indexAtRow += 2)
                {
                    Index index = new Index(row, indexAtRow);
                    Keccak? hash = LoadValue(index);
                    Index siblingIndex = index.Sibling();
                    Keccak? siblingHash = siblingIndex.IndexAtRow >= nodesToIterate ? ZeroHash : LoadValue(siblingIndex);
                    // _logger.Warn($"{siblingIndex.NodeIndex}=>{siblingHash} {index.NodeIndex}=>{hash}");
                    byte[] parentHash = new byte[32];
                    if (hash == Keccak.Zero && siblingHash == Keccak.Zero)
                    {
                        parentHash = Keccak.Zero.Bytes;
                    }
                    else
                    {
                        Hash(hash.Bytes.AsSpan(), siblingHash.Bytes.AsSpan(), parentHash);    
                    }
                    
                    Index parentIndex = index.Parent();
                    SaveValue(parentIndex, parentHash);
                }

                nodesToIterate = nodesToIterate / 2 + nodesToIterate % 2;
                var newStartingNode = startingRowIndex / 2;
                startingRowIndex = newStartingNode - newStartingNode % 2;
            }

            Root = LoadValue(new Index(0, 0));
        }

        public static ulong GetParentIndex(in ulong nodeIndex)
        {
            return new Index(nodeIndex).Parent().NodeIndex;
        }

        public Keccak Root { get; set; }

        public override string ToString()
        {
            return $"[BaselineTree|{Metadata.DbPrefix.ToHexString()}|{Count}]";
        }

        protected abstract void Hash(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, Span<byte> target);

        public readonly ref struct Index
        {
            public Index(ulong nodeIndex)
            {
                ValidateNodeIndex(nodeIndex);

                Row = CalculateRow(nodeIndex);
                IndexAtRow = CalculateIndexAtRow(Row, nodeIndex);
                NodeIndex = nodeIndex;
            }

            public Index(uint row, ulong nodeIndex)
            {
                ValidateRow(row);
                ValidateNodeIndex(row, nodeIndex);

                Row = row;
                IndexAtRow = CalculateIndexAtRow(row, nodeIndex);
                NodeIndex = nodeIndex;
            }

            public Index(uint row, uint indexAtRow)
            {
                ValidateRow(row);
                ValidateIndexAtRow(row, indexAtRow);

                Row = row;
                NodeIndex = CalculateNodeIndex(row, indexAtRow);
                IndexAtRow = indexAtRow;
            }

            public uint Row { get; }
            public uint IndexAtRow { get; }
            public ulong NodeIndex { get; }

            internal bool IsLeftSibling()
            {
                return IndexAtRow % 2 == 0;
            }

            internal Index Parent()
            {
                if (Row == 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(Row), "Root node has no parent");
                }

                return new Index(Row - 1, (NodeIndex + 1) / 2 - 1);
            }

            internal Index Sibling()
            {
                if (Row == 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(Row), "Root node has no siblings.");
                }

                return new Index(Row, IndexAtRow ^ 1);
            }

            private static uint CalculateIndexAtRow(in uint row, in ulong nodeIndex)
            {
                return (uint)(nodeIndex - ((1ul << (int)row) - 1));
            }

            private static ulong CalculateNodeIndex(in uint row, in uint indexAtRow)
            {
                return (1ul << (int)row) - 1u + indexAtRow;
            }

            private static uint CalculateRow(in ulong nodeIndex)
            {
                ValidateNodeIndex(nodeIndex);
                for (uint row = 0; row < LeafRow; row++)
                {
                    if (2ul << (int)row >= nodeIndex + 2)
                    {
                        return row;
                    }
                }

                return LeafRow;
            }

            private static void ValidateRow(in uint row)
            {
                if (row > LeafRow)
                {
                    throw new ArgumentOutOfRangeException($"Tree level should be between 0 and {LeafRow}");
                }
            }

            private static void ValidateIndexAtRow(uint row, uint indexAtRow)
            {
                uint maxIndexAtRow = (uint)((1ul << (int)row) - 1u);
                if (indexAtRow > maxIndexAtRow)
                {
                    throw new ArgumentOutOfRangeException($"Tree level {row} should only have indices between 0 and {maxIndexAtRow}");
                }
            }

            private static void ValidateNodeIndex(ulong nodeIndex)
            {
                if (nodeIndex > MaxNodeIndex)
                {
                    throw new ArgumentOutOfRangeException($"Node index should be between 0 and {MaxNodeIndex}");
                }
            }

            private static void ValidateNodeIndex(in uint row, in ulong nodeIndex)
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

            public override string ToString()
            {
                return $"{NodeIndex} | ({Row},{IndexAtRow})";
            }
        }
    }
}
