// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Collections;
using Nethermind.Core.Extensions;
using Nethermind.Core.Verkle;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Verkle.Curve;
using Nethermind.Verkle.Fields.FrEElement;
using Nethermind.Verkle.Tree.Interfaces;
using Nethermind.Verkle.Tree.Nodes;
using Nethermind.Verkle.Tree.Sync;
using Nethermind.Verkle.Tree.Utils;
using Nethermind.Verkle.Tree.VerkleDb;

namespace Nethermind.Verkle.Tree;

public partial class VerkleTree: IVerkleTree
{
    private static byte[] RootKey => Array.Empty<byte>();
    private readonly ILogger _logger;

    /// <summary>
    /// _leafUpdateCache, _treeCache, _verkleStateStore - these are use while inserting and commiting data to the tree
    ///
    /// Insertion - _leafUpdateCache is used to keep track of keys inserted accumulated by the stem
    /// Commit - calculate and update the commitment of internal nodes from bottom up and insert into _treeCache
    /// CommitTree - flush all the changes stored in _treeCache to _verkleStateStore indexed by the blockNumber
    /// </summary>

    // aggregate the stem commitments here and then update the entire tree when Commit() is called
    private readonly SpanDictionary<byte, LeafUpdateDelta> _leafUpdateCache = new(Bytes.SpanEqualityComparer);

    // cache to maintain recently used or inserted nodes of the tree - should be consistent
    private VerkleMemoryDb _treeCache = new();

    // the store that is responsible to store the tree in a key-value store
    public readonly IVerkleTrieStore _verkleStateStore;

    public VerkleTree(IDbProvider dbProvider, ILogManager logManager)
    {
        _verkleStateStore = new VerkleStateStore(dbProvider, logManager);
        _logger = logManager?.GetClassLogger<VerkleTree>() ?? throw new ArgumentNullException(nameof(logManager));
    }

    public VerkleTree(IVerkleTrieStore verkleStateStore, ILogManager logManager)
    {
        _verkleStateStore = verkleStateStore;
        _logger = logManager?.GetClassLogger<VerkleTree>() ?? throw new ArgumentNullException(nameof(logManager));
    }

    public VerkleCommitment StateRoot
    {
        get => GetStateRoot();
        set => MoveToStateRoot(value);
    }

    private VerkleCommitment GetStateRoot()
    {
        bool inTreeCache = _treeCache.GetInternalNode(Array.Empty<byte>(), out InternalNode? value);
        return inTreeCache ? new VerkleCommitment(value!.Bytes) : _verkleStateStore.StateRoot;
    }

    public bool MoveToStateRoot(VerkleCommitment stateRoot)
    {
        try
        {
            if (_logger.IsTrace) _logger.Trace($"MoveToStateRoot: from: {StateRoot} to: {stateRoot}");
            return _verkleStateStore.MoveToStateRoot(stateRoot);
        }
        catch (Exception e)
        {
            _logger.Error($"MoveToStateRoot: failed | from: {StateRoot} to: {stateRoot}", e);
            return false;
        }
    }

    public byte[]? Get(Pedersen key)
    {
        _treeCache.GetLeaf(key.Bytes, out byte[]? value);
        value ??= _verkleStateStore.GetLeaf(key.Bytes);
        return value;
    }

    private void SetLeafCache(Pedersen key, byte[]? value)
    {
        _treeCache.SetLeaf(key.BytesAsSpan, value);
    }

    public void Insert(Pedersen key, ReadOnlySpan<byte> value)
    {
        ReadOnlySpan<byte> stem = key.StemAsSpan;
        bool present = _leafUpdateCache.TryGetValue(stem, out LeafUpdateDelta leafUpdateDelta);
        if (!present) leafUpdateDelta = new LeafUpdateDelta();
        leafUpdateDelta.UpdateDelta(UpdateLeafAndGetDelta(key, value.ToArray()), key.SuffixByte);
        _leafUpdateCache[stem] = leafUpdateDelta;
    }

    public void InsertStemBatch(ReadOnlySpan<byte> stem, IEnumerable<(byte, byte[])> leafIndexValueMap)
    {
        bool present = _leafUpdateCache.TryGetValue(stem, out LeafUpdateDelta leafUpdateDelta);
        if(!present) leafUpdateDelta = new LeafUpdateDelta();

        Span<byte> key = new byte[32];
        stem.CopyTo(key);
        foreach ((byte index, byte[] value) in leafIndexValueMap)
        {
            key[31] = index;
            leafUpdateDelta.UpdateDelta(UpdateLeafAndGetDelta(new Pedersen(key.ToArray()), value), key[31]);
        }

        _leafUpdateCache[stem.ToArray()] = leafUpdateDelta;
    }

    public void InsertStemBatch(ReadOnlySpan<byte> stem, IEnumerable<LeafInSubTree> leafIndexValueMap)
    {
        bool present = _leafUpdateCache.TryGetValue(stem, out LeafUpdateDelta leafUpdateDelta);
        if(!present) leafUpdateDelta = new LeafUpdateDelta();

        Span<byte> key = new byte[32];
        stem.CopyTo(key);
        foreach (LeafInSubTree leaf in leafIndexValueMap)
        {
            key[31] = leaf.SuffixByte;
            leafUpdateDelta.UpdateDelta(UpdateLeafAndGetDelta(new Pedersen(key.ToArray()), leaf.Leaf), key[31]);
        }

        _leafUpdateCache[stem.ToArray()] = leafUpdateDelta;
    }

    public void InsertStemBatch(in Stem stem, IEnumerable<LeafInSubTree> leafIndexValueMap)
    {
        InsertStemBatch(stem.BytesAsSpan, leafIndexValueMap);
    }

    private Banderwagon UpdateLeafAndGetDelta(Pedersen key, byte[] value)
    {
        byte[]? oldValue = Get(key);
        Banderwagon leafDeltaCommitment = GetLeafDelta(oldValue, value, key.SuffixByte);
        SetLeafCache(key, value);
        return leafDeltaCommitment;
    }

    private static Banderwagon GetLeafDelta(byte[]? oldValue, byte[] newValue, byte index)
    {
        // break the values to calculate the commitments for the leaf
        (FrE newValLow, FrE newValHigh) = VerkleUtils.BreakValueInLowHigh(newValue);
        (FrE oldValLow, FrE oldValHigh) = VerkleUtils.BreakValueInLowHigh(oldValue);

        int posMod128 = index % 128;
        int lowIndex = 2 * posMod128;
        int highIndex = lowIndex + 1;

        Banderwagon deltaLow = Committer.ScalarMul(newValLow - oldValLow, lowIndex);
        Banderwagon deltaHigh = Committer.ScalarMul(newValHigh - oldValHigh, highIndex);
        return deltaLow + deltaHigh;
    }

    private static Banderwagon GetLeafDelta(byte[] newValue, byte index)
    {
        (FrE newValLow, FrE newValHigh) = VerkleUtils.BreakValueInLowHigh(newValue);

        int posMod128 = index % 128;
        int lowIndex = 2 * posMod128;
        int highIndex = lowIndex + 1;

        Banderwagon deltaLow = Committer.ScalarMul(newValLow, lowIndex);
        Banderwagon deltaHigh = Committer.ScalarMul(newValHigh, highIndex);
        return deltaLow + deltaHigh;
    }

    public void Commit(bool forSync = false)
    {
        if (_logger.IsDebug) _logger.Debug($"VT Commit: SubTree Count:{_leafUpdateCache.Count}");
        foreach (KeyValuePair<byte[], LeafUpdateDelta> leafDelta in _leafUpdateCache)
        {
            if (_logger.IsTrace)
                _logger.Trace(
                    $"VT Commit: Stem:{leafDelta.Key.ToHexString()} DeltaCommitment C1:{leafDelta.Value.DeltaC1?.ToBytes().ToHexString()} C2{leafDelta.Value.DeltaC2?.ToBytes().ToHexString()}");
            UpdateTreeCommitments(leafDelta.Key, leafDelta.Value, forSync);
        }
        _leafUpdateCache.Clear();
    }

    public void CommitTree(long blockNumber)
    {
        _verkleStateStore.Flush(blockNumber, _treeCache);
        _treeCache = new VerkleMemoryDb();
    }

    private void UpdateRootNode(Banderwagon rootDelta)
    {
        InternalNode root = GetInternalNode(RootKey) ?? throw new InvalidOperationException("root should be present");
        InternalNode newRoot = root.Clone();
        newRoot.InternalCommitment.AddPoint(rootDelta);
        SetInternalNode(RootKey, newRoot);
    }

    private InternalNode? GetInternalNode(ReadOnlySpan<byte> nodeKey)
    {
        return _treeCache.GetInternalNode(nodeKey, out InternalNode? value)
            ? value
            : _verkleStateStore.GetInternalNode(nodeKey);
    }

    private void SetInternalNode(byte[] nodeKey, InternalNode node, bool replace = true)
    {
        if (replace || !_treeCache.InternalTable.TryGetValue(nodeKey, out InternalNode? prevNode))
        {
            _treeCache.SetInternalNode(nodeKey, node);
        }
        else
        {
            prevNode!.C1 ??= node.C1;
            prevNode.C2 ??= node.C2;
            _treeCache.SetInternalNode(nodeKey, prevNode);
        }
    }

    public void Reset()
    {
        _leafUpdateCache.Clear();
        ProofBranchPolynomialCache.Clear();
        ProofStemPolynomialCache.Clear();
        _treeCache = new VerkleMemoryDb();
        _verkleStateStore.Reset();
    }
}
