// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Core.Verkle;
using Nethermind.Trie.Pruning;
using Nethermind.Verkle.Tree.Interfaces;
using Nethermind.Verkle.Tree.Nodes;
using Nethermind.Verkle.Tree.Sync;
using Nethermind.Verkle.Tree.VerkleDb;

namespace Nethermind.Verkle.Tree;

public class ReadOnlyVerkleStateStore : IVerkleTrieStore, ISyncTrieStore
{
    private VerkleStateStore _verkleStateStore;
    private VerkleMemoryDb _keyValueStore;

    public ReadOnlyVerkleStateStore(VerkleStateStore verkleStateStore, VerkleMemoryDb keyValueStore)
    {
        _verkleStateStore = verkleStateStore;
        _keyValueStore = keyValueStore;
    }

    public VerkleCommitment StateRoot => _verkleStateStore.StateRoot;

    public byte[]? GetLeaf(ReadOnlySpan<byte> key)
    {
        if (_keyValueStore.GetLeaf(key, out byte[]? value)) return value;
        return _verkleStateStore.GetLeaf(key);
    }
    public InternalNode? GetInternalNode(ReadOnlySpan<byte> key)
    {
        if (_keyValueStore.GetInternalNode(key, out var value)) return value;
        return _verkleStateStore.GetInternalNode(key);
    }
    public void SetLeaf(ReadOnlySpan<byte> leafKey, byte[] leafValue)
    {
        _keyValueStore.SetLeaf(leafKey, leafValue);
    }
    public void SetInternalNode(ReadOnlySpan<byte> InternalNodeKey, InternalNode internalNodeValue)
    {
        _keyValueStore.SetInternalNode(InternalNodeKey, internalNodeValue);
    }
    public void Flush(long blockNumber, VerkleMemoryDb batch) { }

    public void ReverseState() { }
    public void ApplyDiffLayer(BatchChangeSet changeSet) { }

    public bool GetForwardMergedDiff(long fromBlock, long toBlock, out VerkleMemoryDb diff)
    {
        return _verkleStateStore.GetForwardMergedDiff(fromBlock, toBlock, out diff);
    }
    public bool GetReverseMergedDiff(long fromBlock, long toBlock, out VerkleMemoryDb diff)
    {
        return _verkleStateStore.GetReverseMergedDiff(fromBlock, toBlock, out diff);
    }

    public VerkleCommitment GetStateRoot()
    {
        return _verkleStateStore.GetStateRoot();
    }
    public bool MoveToStateRoot(VerkleCommitment stateRoot)
    {
        return _verkleStateStore.MoveToStateRoot(stateRoot);
    }

    public void Reset() => _verkleStateStore.Reset();

    public event EventHandler<ReorgBoundaryReached>? ReorgBoundaryReached;

    public ReadOnlyVerkleStateStore AsReadOnly(VerkleMemoryDb keyValueStore)
    {
        return new ReadOnlyVerkleStateStore(_verkleStateStore, keyValueStore);
    }
    public bool IsFullySynced(Keccak stateRoot)
    {
        return _verkleStateStore.IsFullySynced(stateRoot);
    }

    public IEnumerable<KeyValuePair<byte[], byte[]>> GetLeafRangeIterator(byte[] fromRange, byte[] toRange, long blockNumber)
    {
        return _verkleStateStore.GetLeafRangeIterator(fromRange, toRange, blockNumber);
    }

    public IEnumerable<PathWithSubTree> GetLeafRangeIterator(Stem fromRange, Stem toRange, VerkleCommitment stateRoot, long bytes)
    {
        return _verkleStateStore.GetLeafRangeIterator(fromRange, toRange, stateRoot, bytes);
    }
}
