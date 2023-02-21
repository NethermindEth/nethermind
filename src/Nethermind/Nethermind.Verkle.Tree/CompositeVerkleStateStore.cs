// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Trie.Pruning;
using Nethermind.Verkle.Tree.Nodes;
using Nethermind.Verkle.Tree.VerkleDb;

namespace Nethermind.Verkle.Tree;

public class CompositeVerkleStateStore: IVerkleStore
{

    private readonly IVerkleStore _wrappedStore;
    private VerkleMemoryDb _memDb;

    public CompositeVerkleStateStore(IVerkleStore verkleStore)
    {
        _wrappedStore = verkleStore ?? throw new ArgumentNullException(nameof(verkleStore));
        _memDb = new VerkleMemoryDb();
    }

    public event EventHandler<ReorgBoundaryReached>? ReorgBoundaryReached;

    public byte[] RootHash
    {
        get => GetStateRoot();
        set => MoveToStateRoot(value);
    }
    public byte[]? GetLeaf(byte[] key)
    {
        return _memDb.GetLeaf(key, out var value) ? value : _wrappedStore.GetLeaf(key);
    }
    public SuffixTree? GetStem(byte[] key)
    {
        return _memDb.GetStem(key, out var value) ? value : _wrappedStore.GetStem(key);
    }
    public InternalNode? GetBranch(byte[] key)
    {
        return _memDb.GetBranch(key, out var value) ? value : _wrappedStore.GetBranch(key);
    }
    public void SetLeaf(byte[] leafKey, byte[] leafValue)
    {
        _memDb.SetLeaf(leafKey, leafValue);
    }
    public void SetStem(byte[] stemKey, SuffixTree suffixTree)
    {
        _memDb.SetStem(stemKey, suffixTree);
    }
    public void SetBranch(byte[] branchKey, InternalNode internalNodeValue)
    {
        _memDb.SetBranch(branchKey, internalNodeValue);
    }
    public void Flush(long blockNumber)
    {
        foreach (KeyValuePair<byte[], byte[]?> entry in _memDb.LeafTable)
        {
            _wrappedStore.SetLeaf(entry.Key, entry.Value);
        }

        foreach (KeyValuePair<byte[], SuffixTree?> entry in _memDb.StemTable)
        {
            _wrappedStore.SetStem(entry.Key, entry.Value);
        }

        foreach (KeyValuePair<byte[], InternalNode?> entry in _memDb.BranchTable)
        {
            _wrappedStore.SetBranch(entry.Key, entry.Value);
        }
        _wrappedStore.Flush(blockNumber);
        _memDb = new VerkleMemoryDb();
    }
    public void ReverseState()
    {
        _memDb = new VerkleMemoryDb();
        _wrappedStore.ReverseState();
    }
    public void ApplyDiffLayer(BatchChangeSet changeSet)
    {
        _memDb = new VerkleMemoryDb();
        _wrappedStore.ApplyDiffLayer(changeSet);
    }
    public byte[] GetStateRoot()
    {
        return GetBranch(Array.Empty<byte>())?._internalCommitment.Point.ToBytes().ToArray() ?? throw new InvalidOperationException();
    }
    public void MoveToStateRoot(byte[] stateRoot)
    {
        _memDb = new VerkleMemoryDb();
        _wrappedStore.MoveToStateRoot(stateRoot);
    }
    public ReadOnlyVerkleStateStore AsReadOnly(VerkleMemoryDb keyValueStore)
    {
        throw new NotImplementedException();
    }
    public VerkleMemoryDb GetForwardMergedDiff(long fromBlock, long toBlock)
    {
        _memDb = new VerkleMemoryDb();
        return _wrappedStore.GetForwardMergedDiff(fromBlock, toBlock);
    }
    public VerkleMemoryDb GetReverseMergedDiff(long fromBlock, long toBlock)
    {
        _memDb = new VerkleMemoryDb();
        return _wrappedStore.GetReverseMergedDiff(fromBlock, toBlock);
    }
}
