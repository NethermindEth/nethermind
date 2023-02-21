// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using Nethermind.Verkle.Tree.Nodes;
using Nethermind.Verkle.Tree.VerkleDb;

namespace Nethermind.Verkle.Tree;

public class ReadOnlyVerkleStateStore: IVerkleStore, ISyncTrieStore
{
    private VerkleStateStore _verkleStateStore;
    private VerkleMemoryDb _keyValueStore;

    public ReadOnlyVerkleStateStore(VerkleStateStore verkleStateStore, VerkleMemoryDb keyValueStore)
    {
        _verkleStateStore = verkleStateStore;
        _keyValueStore = keyValueStore;
    }

    public byte[] RootHash
    {
        get => _verkleStateStore.RootHash;
        set => throw new ArgumentException();
    }
    public byte[]? GetLeaf(byte[] key)
    {
        if (_keyValueStore.GetLeaf(key, out byte[]? value)) return value;
        return _verkleStateStore.GetLeaf(key);
    }
    public SuffixTree? GetStem(byte[] key)
    {
        if (_keyValueStore.GetStem(key, out var value)) return value;
        return _verkleStateStore.GetStem(key);
    }
    public InternalNode? GetBranch(byte[] key)
    {
        if (_keyValueStore.GetBranch(key, out var value)) return value;
        return _verkleStateStore.GetBranch(key);
    }
    public void SetLeaf(byte[] leafKey, byte[] leafValue)
    {
        _keyValueStore.SetLeaf(leafKey, leafValue);
    }
    public void SetStem(byte[] stemKey, SuffixTree suffixTree)
    {
        _keyValueStore.SetStem(stemKey, suffixTree);
    }
    public void SetBranch(byte[] branchKey, InternalNode internalNodeValue)
    {
        _keyValueStore.SetBranch(branchKey, internalNodeValue);
    }
    public void Flush(long blockNumber) { }

    public void ReverseState() { }
    public void ApplyDiffLayer(BatchChangeSet changeSet)
    {
    }
    public byte[] GetStateRoot()
    {
        return _verkleStateStore.GetStateRoot();
    }
    public void MoveToStateRoot(byte[] stateRoot)
    {
        _verkleStateStore.MoveToStateRoot(stateRoot);
    }

    public VerkleMemoryDb GetForwardMergedDiff(long fromBlock, long toBlock)
    {
        throw new NotImplementedException();
    }
    public VerkleMemoryDb GetReverseMergedDiff(long fromBlock, long toBlock)
    {
        throw new NotImplementedException();
    }
    public event EventHandler<ReorgBoundaryReached>? ReorgBoundaryReached;

    public ReadOnlyVerkleStateStore AsReadOnly(VerkleMemoryDb keyValueStore)
    {
        return new ReadOnlyVerkleStateStore(_verkleStateStore, keyValueStore);
    }
    public bool IsFullySynced(Keccak stateRoot)
    {
        return _verkleStateStore.IsFullySynced(stateRoot);
    }
}
