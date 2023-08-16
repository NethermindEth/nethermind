// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core.Crypto;
using Nethermind.Core.Verkle;
using Nethermind.Db;
using Nethermind.Verkle.Tree.Nodes;
using Nethermind.Verkle.Tree.VerkleDb;

namespace Nethermind.Verkle.Tree;

public partial class VerkleStateStore
{

    /// <summary>
    ///  maximum number of blocks that should be stored in cache (not persisted in db)
    /// </summary>
    private int MaxNumberOfBlocksInCache { get; }
    private StackQueue<(long, ReadOnlyVerkleMemoryDb)>? BlockCache { get; }

    private VerkleHistoryStore? History { get; }


    // now the full state back in time by one block.
    public void ReverseState()
    {

        if (BlockCache is not null && BlockCache.Count != 0)
        {
            BlockCache.Pop(out _);
            return;
        }

        VerkleMemoryDb reverseDiff =
            History?.GetBatchDiff(LastPersistedBlockNumber, LastPersistedBlockNumber - 1).DiffLayer ??
            throw new ArgumentException("History not Enabled");

        foreach (KeyValuePair<byte[], byte[]?> entry in reverseDiff.LeafTable)
        {
            reverseDiff.GetLeaf(entry.Key, out byte[]? node);
            if (node is null)
            {
                Storage.RemoveLeaf(entry.Key);
            }
            else
            {
                Storage.SetLeaf(entry.Key, node);
            }
        }

        foreach (KeyValuePair<byte[], InternalNode?> entry in reverseDiff.InternalTable)
        {
            reverseDiff.GetInternalNode(entry.Key, out InternalNode? node);
            if (node is null)
            {
                Storage.RemoveInternalNode(entry.Key);
            }
            else
            {
                Storage.SetInternalNode(entry.Key, node);
            }
        }
        LastPersistedBlockNumber -= 1;
    }

    // use the batch diff to move the full state back in time to access historical state.
    public void ApplyDiffLayer(BatchChangeSet changeSet)
    {
        if (changeSet.FromBlockNumber != LastPersistedBlockNumber)
        {
            throw new ArgumentException(
                $"This case should not be possible. Diff fromBlock should be equal to persisted block number. FullStateBlock:{LastPersistedBlockNumber}!=fromBlock:{changeSet.FromBlockNumber}",
                nameof(changeSet.FromBlockNumber));
        }


        VerkleMemoryDb reverseDiff = changeSet.DiffLayer;

        foreach (KeyValuePair<byte[], byte[]?> entry in reverseDiff.LeafTable)
        {
            reverseDiff.GetLeaf(entry.Key, out byte[]? node);
            if (node is null)
            {
                Storage.RemoveLeaf(entry.Key);
            }
            else
            {
                Storage.SetLeaf(entry.Key, node);
            }
        }

        foreach (KeyValuePair<byte[], InternalNode?> entry in reverseDiff.InternalTable)
        {
            reverseDiff.GetInternalNode(entry.Key, out InternalNode? node);
            if (node is null)
            {
                Storage.RemoveInternalNode(entry.Key);
            }
            else
            {
                Storage.SetInternalNode(entry.Key, node);
            }
        }
        LastPersistedBlockNumber = changeSet.ToBlockNumber;
    }

    public bool MoveToStateRoot(VerkleCommitment stateRoot)
    {
        // TODO: this can be replace with StateRoot - no need to get stateRoot from db
        VerkleCommitment currentRoot = GetStateRoot();
        if (currentRoot == stateRoot) return true;

        if (_logger.IsDebug) _logger.Debug($"Trying to move state root from:{currentRoot} to:{stateRoot}");

        // TODO: this is actually not possible - not sure if return true is correct here
        if (stateRoot.Equals(new VerkleCommitment(Keccak.EmptyTreeHash.Bytes.ToArray())))
        {
            if (currentRoot.Equals(VerkleCommitment.Zero)) return true;
            return false;
        }

        // resolve block numbers
        long fromBlock = StateRootToBlocks[currentRoot];
        if (fromBlock == -1)
        {
            if (_logger.IsDebug) _logger.Debug($"Cannot get the block number for currentRoot:{currentRoot}");
            return false;
        }
        long toBlock = StateRootToBlocks[stateRoot];
        if (toBlock == -1)
        {
            if (_logger.IsDebug) _logger.Debug($"Cannot get the block number for wantedStateRoot:{stateRoot}");
            return false;
        }

        if (_logger.IsDebug)
            _logger.Debug($"Block numbers resolved. Trying to move state from:{fromBlock} to:{toBlock}");

        // TODO: this should be handled when comparing stateRoot before
        if (fromBlock == toBlock) return true;

        if (fromBlock > toBlock)
        {
            long noOfBlockToMove = fromBlock - toBlock;
            if (BlockCache is not null && noOfBlockToMove > BlockCache.Count)
            {
                if (_logger.IsDebug)
                    _logger.Debug(
                        $"Number of blocks to move:{noOfBlockToMove}. Removing all the diffs from BlockCache ({noOfBlockToMove} > {BlockCache.Count})");

                if (History is null)
                {
                    if (_logger.IsDebug) _logger.Debug($"History is null and in this case - state cannot be reverted to wanted state root");
                    return false;
                }
                BlockCache.Clear();
                fromBlock -= BlockCache.Count;

                if (_logger.IsDebug)
                    _logger.Debug($"now using fromBlock:{fromBlock} toBlock:{toBlock}");
                BatchChangeSet batchDiff = History.GetBatchDiff(fromBlock, toBlock);
                ApplyDiffLayer(batchDiff);
            }
            else
            {
                if (_logger.IsDebug)
                    _logger.Debug(
                        $"Number of blocks to move:{noOfBlockToMove}. Removing all the diffs from BlockCache ({noOfBlockToMove} > {BlockCache?.Count})");
                if (BlockCache is not null)
                {
                    for (int i = 0; i < noOfBlockToMove; i++)
                    {
                        BlockCache.Pop(out _);
                    }
                }
                else
                {
                    if (_logger.IsDebug)
                        _logger.Debug(
                            $"BlockCache is null and in this case - state cannot be reverted to wanted state root");
                    return false;
                }
            }
        }
        else
        {
            if (_logger.IsDebug)
                _logger.Debug($"Trying to move forward in state - this is not implemented and supported yet");
            return false;
        }

        Debug.Assert(GetStateRoot().Equals(stateRoot));
        LatestCommittedBlockNumber = toBlock;
        return true;
    }

    // This generates and returns a batchForwardDiff, that can be used to move the full state from fromBlock to toBlock.
    // for this fromBlock < toBlock - move forward in time
    public bool GetForwardMergedDiff(long fromBlock, long toBlock, [MaybeNullWhen(false)]out VerkleMemoryDb diff)
    {
        if (History is null)
        {
            diff = default;
            return false;
        }
        diff = History.GetBatchDiff(fromBlock, toBlock).DiffLayer;
        return true;
    }

    // This generates and returns a batchForwardDiff, that can be used to move the full state from fromBlock to toBlock.
    // for this fromBlock > toBlock - move back in time
    public bool GetReverseMergedDiff(long fromBlock, long toBlock, [MaybeNullWhen(false)]out VerkleMemoryDb diff)
    {
        if (History is null)
        {
            diff = default;
            return false;
        }
        diff = History.GetBatchDiff(fromBlock, toBlock).DiffLayer;
        return true;
    }

    private readonly StateRootToBlockMap StateRootToBlocks;

    private readonly struct StateRootToBlockMap
    {
        private readonly IDb _stateRootToBlock;

        public StateRootToBlockMap(IDb stateRootToBlock)
        {
            _stateRootToBlock = stateRootToBlock;
        }

        public long this[VerkleCommitment key]
        {
            get
            {
                // if (Pedersen.Zero.Equals(key)) return -1;
                byte[]? encodedBlock = _stateRootToBlock[key.Bytes];
                return encodedBlock is null ? -2 : BinaryPrimitives.ReadInt64LittleEndian(encodedBlock);
            }
            set
            {
                Span<byte> encodedBlock = stackalloc byte[8];
                BinaryPrimitives.WriteInt64LittleEndian(encodedBlock, value);
                if(!_stateRootToBlock.KeyExists(key.Bytes))
                    _stateRootToBlock.Set(key.Bytes, encodedBlock.ToArray());
            }
        }
    }
}
