// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Verkle;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Verkle.Curve;
using Nethermind.Verkle.Fields.FrEElement;
using Nethermind.Verkle.Tree.Sync;
using Nethermind.Verkle.Tree.TreeNodes;
using Nethermind.Verkle.Tree.TreeStore;
using Nethermind.Verkle.Tree.Utils;
using Nethermind.Verkle.Tree.VerkleDb;

namespace Nethermind.Verkle.Tree;

public partial class VerkleTree(IVerkleTreeStore verkleStateStore, ILogManager logManager) : IVerkleTree
{
    /// <summary>
    ///     _leafUpdateCache, _treeCache, _verkleStateStore - these are use while inserting and commiting data to the tree
    ///     Insertion - _leafUpdateCache is used to keep track of keys inserted accumulated by the stem
    ///     Commit - calculate and update the commitment of internal nodes from bottom up and insert into _treeCache
    ///     CommitTree - flush all the changes stored in _treeCache to _verkleStateStore indexed by the blockNumber
    /// </summary>

    // aggregate the stem commitments here and then update the entire tree when Commit() is called
    private readonly SpanDictionary<byte, LeafUpdateDelta> _leafUpdateCache = new(Bytes.SpanEqualityComparer);

    private readonly ILogger _logger = logManager?.GetClassLogger<VerkleTree>() ?? throw new ArgumentNullException(nameof(logManager));

    // the store that is responsible to store the tree in a key-value store
    public readonly IVerkleTreeStore _verkleStateStore = verkleStateStore;

    // cache to maintain recently used or inserted nodes of the tree - should be consistent
    public VerkleMemoryDb _treeCache = new();

    private static byte[] RootKey => Array.Empty<byte>();

    public Hash256 StateRoot
    {
        get => GetStateRoot();
        set => MoveToStateRoot(value);
    }

    public bool MoveToStateRoot(Hash256 stateRoot)
    {
        try
        {
            if (_logger.IsTrace) _logger.Trace($"MoveToStateRoot: from: {StateRoot} to: {stateRoot}");
            _treeCache.LeafTable.Clear();
            _treeCache.InternalTable.Clear();
            return _verkleStateStore.MoveToStateRoot(stateRoot);
        }
        catch (Exception e)
        {
            _logger.Error($"MoveToStateRoot: failed | from: {StateRoot} to: {stateRoot}", e);
            return false;
        }
    }

    public byte[]? Get(Hash256 key, Hash256? stateRoot = null)
    {
        _treeCache.GetLeaf(key.Bytes, out var value);
        value ??= _verkleStateStore.GetLeaf(key.Bytes, stateRoot);
        return value;
    }

    public void Insert(Hash256 key, in ReadOnlySpan<byte> value)
    {
        ReadOnlySpan<byte> stem = key.Bytes.Slice(0, 31);
        var present = _leafUpdateCache.TryGetValue(stem, out LeafUpdateDelta leafUpdateDelta);
        if (!present) leafUpdateDelta = new LeafUpdateDelta();
        leafUpdateDelta.UpdateDelta(UpdateLeafAndGetDelta(key, value.ToArray()), key.Bytes[31]);
        _leafUpdateCache[stem] = leafUpdateDelta;
    }

    public void InsertStemBatch(in ReadOnlySpan<byte> stem, IEnumerable<(byte, byte[])> leafIndexValueMap)
    {
        var present = _leafUpdateCache.TryGetValue(stem, out LeafUpdateDelta leafUpdateDelta);
        if (!present) leafUpdateDelta = new LeafUpdateDelta();

        Span<byte> key = new byte[32];
        stem.CopyTo(key);
        foreach ((var index, var value) in leafIndexValueMap)
        {
            key[31] = index;
            leafUpdateDelta.UpdateDelta(UpdateLeafAndGetDelta(new Hash256(key.ToArray()), value), key[31]);
        }

        _leafUpdateCache[stem] = leafUpdateDelta;
    }

    public void InsertStemBatch(in ReadOnlySpan<byte> stem, IEnumerable<LeafInSubTree> leafIndexValueMap)
    {
        var present = _leafUpdateCache.TryGetValue(stem, out LeafUpdateDelta leafUpdateDelta);
        if (!present) leafUpdateDelta = new LeafUpdateDelta();

        Span<byte> key = new byte[32];
        stem.CopyTo(key);
        foreach (LeafInSubTree leaf in leafIndexValueMap)
        {
            key[31] = leaf.SuffixByte;
            leafUpdateDelta.UpdateDelta(UpdateLeafAndGetDelta(new Hash256(key.ToArray()), leaf.Leaf), key[31]);
        }

        _leafUpdateCache[stem.ToArray()] = leafUpdateDelta;
    }

    public void InsertStemBatch(Stem stem, IEnumerable<LeafInSubTree> leafIndexValueMap)
    {
        InsertStemBatch(stem.BytesAsSpan, leafIndexValueMap);
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
        _verkleStateStore.InsertBatch(blockNumber, _treeCache);
        Reset();
    }

    private Hash256 GetStateRoot()
    {
        var inTreeCache = _treeCache.GetInternalNode(Array.Empty<byte>(), out InternalNode? value);
        return inTreeCache ? new Hash256(value!.Bytes) : _verkleStateStore.StateRoot;
    }

    private void SetLeafCache(Hash256 key, byte[]? value)
    {
        _treeCache.SetLeaf(key.Bytes, value);
    }

    private Banderwagon UpdateLeafAndGetDelta(Hash256 key, byte[] value)
    {
        var oldValue = Get(key);
        Banderwagon leafDeltaCommitment = GetLeafDelta(oldValue, value, key.Bytes[31]);
        SetLeafCache(key, value);
        return leafDeltaCommitment;
    }

    private static Banderwagon GetLeafDelta(byte[]? oldValue, byte[] newValue, byte index)
    {
        // break the values to calculate the commitments for the leaf
        (FrE newValLow, FrE newValHigh) = VerkleUtils.BreakValueInLowHigh(newValue);
        (FrE oldValLow, FrE oldValHigh) = VerkleUtils.BreakValueInLowHigh(oldValue);

        var posMod128 = index % 128;
        var lowIndex = 2 * posMod128;
        var highIndex = lowIndex + 1;

        Banderwagon deltaLow = Committer.ScalarMul(newValLow - oldValLow, lowIndex);
        Banderwagon deltaHigh = Committer.ScalarMul(newValHigh - oldValHigh, highIndex);
        return deltaLow + deltaHigh;
    }

    private static Banderwagon GetLeafDelta(byte[] newValue, byte index)
    {
        (FrE newValLow, FrE newValHigh) = VerkleUtils.BreakValueInLowHigh(newValue);

        var posMod128 = index % 128;
        var lowIndex = 2 * posMod128;
        var highIndex = lowIndex + 1;

        Banderwagon deltaLow = Committer.ScalarMul(newValLow, lowIndex);
        Banderwagon deltaHigh = Committer.ScalarMul(newValHigh, highIndex);
        return deltaLow + deltaHigh;
    }

    private void UpdateRootNode(in Banderwagon rootDelta)
    {
        InternalNode root = GetInternalNode(RootKey) ?? throw new InvalidOperationException("root should be present");
        InternalNode newRoot = root.Clone();
        newRoot.InternalCommitment.AddPoint(rootDelta);
        SetInternalNode(RootKey, newRoot);
    }

    public byte[]? Get(in ReadOnlySpan<byte> key, Hash256? stateRoot = null)
    {
        return _treeCache.GetLeaf(key, out var value) ? value : _verkleStateStore.GetLeaf(key, stateRoot);
    }

    private InternalNode? GetInternalNode(in ReadOnlySpan<byte> nodeKey, Hash256? rootHash = null)
    {
        return _treeCache.GetInternalNode(nodeKey, out InternalNode? value)
            ? value
            : _verkleStateStore.GetInternalNode(nodeKey, rootHash);
    }

    private void SetInternalNode(in ReadOnlySpan<byte> nodeKey, InternalNode node, bool replace = true)
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
    }
}
