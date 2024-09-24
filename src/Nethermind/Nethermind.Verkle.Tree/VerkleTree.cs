// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    private readonly SpanConcurrentDictionary<byte, LeafUpdateDelta> _leafUpdateCache = new(Bytes.SpanEqualityComparer);

    private readonly ILogger _logger = logManager?.GetClassLogger<VerkleTree>() ?? throw new ArgumentNullException(nameof(logManager));

    // the store that is responsible to store the tree in a key-value store
    public readonly IVerkleTreeStore VerkleStateStore = verkleStateStore;

    // cache to maintain recently used or inserted nodes of the tree - should be consistent
    public VerkleMemoryDb TreeCache = new();

    private static byte[] RootKey => [];

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
            TreeCache.LeafTable.Clear();
            TreeCache.InternalTable.Clear();
            return VerkleStateStore.MoveToStateRoot(stateRoot);
        }
        catch (Exception e)
        {
            _logger.Error($"MoveToStateRoot: failed | from: {StateRoot} to: {stateRoot}", e);
            return false;
        }
    }

    public byte[]? Get(Hash256 key, Hash256? stateRoot = null)
    {
        TreeCache.GetLeaf(key.Bytes, out var value);
        value ??= VerkleStateStore.GetLeaf(key.Bytes, stateRoot);
        return value;
    }

    public bool HasLeaf(Hash256 key, Hash256? stateRoot = null)
    {
        return TreeCache.HasLeaf(key.Bytes) || VerkleStateStore.HasLeaf(key.Bytes);
    }

    public void Insert(Hash256 key, in ReadOnlySpan<byte> value)
    {
        ReadOnlySpan<byte> stem = key.Bytes[..31];
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
        foreach (var (index, value) in leafIndexValueMap)
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
        if (forSync)
        {
            CommitOneByOne(forSync);
        }
        else
        {
            CommitBulk();
        }
    }

    private void CommitOneByOne(bool forSync = false)
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
        VerkleStateStore.InsertBatch(blockNumber, TreeCache);
        Reset();
    }

    private Hash256 GetStateRoot()
    {
        var inTreeCache = TreeCache.GetInternalNode(Array.Empty<byte>(), out InternalNode? value);
        return inTreeCache ? new Hash256(value!.Bytes) : VerkleStateStore.StateRoot;
    }

    private void SetLeafCache(Hash256 key, byte[]? value)
    {
        TreeCache.SetLeaf(key.Bytes, value);
    }

    private Banderwagon UpdateLeafAndGetDelta(Hash256 key, byte[] value)
    {
        var oldValue = Get(key);

        byte[] data = new byte[64];
        if (oldValue is null)
            VerkleCrypto.GetLeafDeltaNewValue(key.Bytes[31], value, data);
        else
            VerkleCrypto.GetLeafDeltaBothValue(key.Bytes[31], oldValue, value, data);

        var leafDeltaCommitment = Banderwagon.FromBytesUncompressedUnchecked(data, false);
        SetLeafCache(key, value);
        return leafDeltaCommitment;
    }

    // used during syncing, we download the entire tree from scratch - there is not existing value
    private static Banderwagon GetLeafDelta(byte[] newValue, byte index)
    {
        byte[] data = new byte[64];
        VerkleCrypto.GetLeafDeltaNewValue(index, newValue, data);
        return Banderwagon.FromBytesUncompressedUnchecked(data, false);
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
        return TreeCache.GetLeaf(key, out var value) ? value : VerkleStateStore.GetLeaf(key, stateRoot);
    }

    private InternalNode? GetInternalNode(in ReadOnlySpan<byte> nodeKey, Hash256? rootHash = null)
    {
        return TreeCache.GetInternalNode(nodeKey, out InternalNode? value)
            ? value
            : VerkleStateStore.GetInternalNode(nodeKey, rootHash);
    }

    private void SetInternalNode(in ReadOnlySpan<byte> nodeKey, InternalNode node, bool replace = true)
    {
        if (replace || !TreeCache.InternalTable.TryGetValue(nodeKey, out InternalNode? prevNode))
        {
            TreeCache.SetInternalNode(nodeKey, node);
        }
        else
        {
            prevNode!.C1 ??= node.C1;
            prevNode.C2 ??= node.C2;
            TreeCache.SetInternalNode(nodeKey, prevNode);
        }
    }

    public void Reset()
    {
        _leafUpdateCache.Clear();
        ProofBranchPolynomialCache.Clear();
        ProofStemPolynomialCache.Clear();
        TreeCache = new VerkleMemoryDb();
    }
}
