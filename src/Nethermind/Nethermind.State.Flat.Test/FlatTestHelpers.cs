// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.PersistedSnapshots;
using Nethermind.Trie;
using Nethermind.Trie.NodeLog;
using NSubstitute;

namespace Nethermind.State.Flat.Test;

public static class FlatTestHelpers
{
    public static Snapshot MakeSnapshot(IResourcePool pool, Action<SnapshotContent>? populate = null)
    {
        SnapshotContent content = pool.GetSnapshotContent(ResourcePool.Usage.MainBlockProcessing);
        populate?.Invoke(content);
        return new Snapshot(StateId.PreGenesis, StateId.PreGenesis, content, pool, ResourcePool.Usage.MainBlockProcessing);
    }

    /// <summary>A log with 64 KiB generations, so tests can rotate it cheaply.</summary>
    public static TrieNodeLog NewTestLog() => new(0, 64 * 1024);

    public const string LeafRlpA = "c482200101";
    public const string LeafRlpB = "c482200102";
    public const string LeafRlpC = "c482200103";

    /// <summary>Creates a snapshot owning a retain on the active generation of <paramref name="log"/>.</summary>
    public static Snapshot CreateSnapshot(this IResourcePool pool, in StateId from, in StateId to, ResourcePool.Usage usage, TrieNodeLog log) =>
        new(from, to, pool.GetSnapshotContent(usage), pool, usage) { LogRetain = log.RetainActive() };

    /// <summary>Appends records until <paramref name="log"/> starts a new generation.</summary>
    public static void StartNewGeneration(TrieNodeLog log)
    {
        byte[] leaf = Bytes.FromHexString(LeafRlpA);
        ulong generation = AppendNode(log, null, TreePath.Empty, leaf) >> 32;
        while (AppendNode(log, null, TreePath.Empty, leaf) >> 32 == generation) { }
    }

    /// <summary>Makes a snapshot owning a retain on <paramref name="log"/> taken before <paramref name="populate"/> writes its nodes.</summary>
    public static Snapshot MakeSnapshot(IResourcePool pool, TrieNodeLog log, Action<SnapshotContent> populate)
    {
        Snapshot snapshot = pool.CreateSnapshot(StateId.PreGenesis, StateId.PreGenesis, ResourcePool.Usage.MainBlockProcessing, log);
        populate(snapshot.Content);
        return snapshot;
    }

    /// <summary>Records the node <paramref name="rlpHex"/> in <paramref name="log"/> and sets its pointer as the state node at <paramref name="path"/>.</summary>
    public static ulong SetStateNodeRlp(this SnapshotContent content, TrieNodeLog log, in TreePath path, string rlpHex) =>
        content.SetStateNodeRlp(log, path, Bytes.FromHexString(rlpHex));

    /// <inheritdoc cref="SetStateNodeRlp(SnapshotContent, TrieNodeLog, in TreePath, string)"/>
    public static ulong SetStateNodeRlp(this SnapshotContent content, TrieNodeLog log, in TreePath path, byte[] rlp) =>
        content.StateNodes[path] = AppendNode(log, null, path, rlp);

    /// <summary>Records the node <paramref name="rlpHex"/> in <paramref name="log"/> and sets its pointer as the storage node of <paramref name="address"/> at <paramref name="path"/>.</summary>
    public static ulong SetStorageNodeRlp(this SnapshotContent content, TrieNodeLog log, Hash256 address, in TreePath path, string rlpHex) =>
        content.SetStorageNodeRlp(log, address, path, Bytes.FromHexString(rlpHex));

    /// <inheritdoc cref="SetStorageNodeRlp(SnapshotContent, TrieNodeLog, Hash256, in TreePath, string)"/>
    public static ulong SetStorageNodeRlp(this SnapshotContent content, TrieNodeLog log, Hash256 address, in TreePath path, byte[] rlp) =>
        content.StorageNodes[(address, path)] = AppendNode(log, address, path, rlp);

    /// <summary>The RLP of a leaf with an empty key holding <paramref name="value"/>, a distinct valid trie node per value.</summary>
    public static byte[] LeafRlp(params byte[] value)
    {
        byte[] rlp = new byte[NodeRlp.LeafLength([], value)];
        NodeRlp.WriteLeaf([], value, rlp);
        return rlp;
    }

    private static ulong AppendNode(TrieNodeLog log, Hash256? address, in TreePath path, byte[] rlp) =>
        log.AppendRecordFromRlp(address, path, ValueKeccak.Compute(rlp), rlp);

    public static SnapshotPooledList SnapshotList(params Snapshot[] snapshots)
    {
        SnapshotPooledList list = new(snapshots.Length == 0 ? 1 : snapshots.Length);
        foreach (Snapshot s in snapshots) list.Add(s);
        return list;
    }

    /// <summary>
    /// Builds a single-snapshot <see cref="ReadOnlySnapshotBundle"/> backed by a substitute persistence reader,
    /// optionally pre-populating the snapshot content via <paramref name="populate"/>.
    /// </summary>
    public static ReadOnlySnapshotBundle MakeBundle(ResourcePool pool, Action<SnapshotContent>? populate = null) =>
        MakeBundle(pool, Substitute.For<IPersistence.IPersistenceReader>(), populate);

    /// <inheritdoc cref="MakeBundle(ResourcePool, Action{SnapshotContent})"/>
    /// <param name="reader">Persistence reader to back the bundle with, for tests that assert on its reads.</param>
    public static ReadOnlySnapshotBundle MakeBundle(
        ResourcePool pool,
        IPersistence.IPersistenceReader reader,
        Action<SnapshotContent>? populate = null) =>
        new(SnapshotList(MakeSnapshot(pool, populate)), reader,
            recordDetailedMetrics: false, PersistedSnapshotStack.Empty());

    /// <inheritdoc cref="MakeBundle(ResourcePool, Action{SnapshotContent})"/>
    /// <param name="log">The log <paramref name="populate"/> records nodes in; the snapshot retains it.</param>
    public static ReadOnlySnapshotBundle MakeBundle(ResourcePool pool, TrieNodeLog log, Action<SnapshotContent> populate) =>
        new(SnapshotList(MakeSnapshot(pool, log, populate)), Substitute.For<IPersistence.IPersistenceReader>(),
            recordDetailedMetrics: false, PersistedSnapshotStack.Empty());
}

/// <summary>
/// Hand-rolled fake for <see cref="IPersistence.IWriteBatch"/> that records every call into public lists.
/// Used in tests in place of <c>Substitute.For&lt;IPersistence.IWriteBatch&gt;()</c> because Castle DynamicProxy
/// (NSubstitute's backing) throws <c>InvalidProgramException</c> when proxying methods with ref-struct parameters
/// such as <c>scoped ReadOnlySpan&lt;byte&gt;</c>.
/// </summary>
internal sealed class FakeWriteBatch : IPersistence.IWriteBatch
{
    public List<Address> SelfDestructCalls { get; } = [];
    public List<(Address Addr, Account? Account)> SetAccountCalls { get; } = [];
    public List<(Address Addr, UInt256 Slot, UInt256? Value)> SetStorageCalls { get; } = [];
    public List<(TreePath Path, byte[] Rlp)> SetStateTrieNodeCalls { get; } = [];
    public List<(Hash256 Address, TreePath Path, byte[] Rlp)> SetStorageTrieNodeCalls { get; } = [];
    public List<(ValueHash256 AddrHash, ValueHash256 SlotHash, byte[] RlpValue)> SetStorageRawEncodedCalls { get; } = [];
    public List<(ValueHash256 AddrHash, Account Account)> SetAccountRawCalls { get; } = [];
    public List<(ValueHash256 FromPath, ValueHash256 ToPath)> DeleteAccountRangeCalls { get; } = [];
    public List<(ValueHash256 AddressHash, ValueHash256 FromPath, ValueHash256 ToPath)> DeleteStorageRangeCalls { get; } = [];
    public List<(ValueHash256 From, ValueHash256 To)> DeleteStateTrieNodeRangeCalls { get; } = [];
    public List<(ValueHash256 AddressHash, ValueHash256 From, ValueHash256 To)> DeleteStorageTrieNodeRangeCalls { get; } = [];
    public int DisposeCount { get; private set; }

    public void SelfDestruct(Address addr) => SelfDestructCalls.Add(addr);
    public void SetAccount(Address addr, Account? account) => SetAccountCalls.Add((addr, account));
    public void SetStorage(Address addr, in UInt256 slot, in UInt256? value) => SetStorageCalls.Add((addr, slot, value));

    public void SetStateTrieNode(in TreePath path, scoped ReadOnlySpan<byte> rlp) =>
        SetStateTrieNodeCalls.Add((path, rlp.ToArray()));

    public void SetStorageTrieNode(Hash256 address, in TreePath path, scoped ReadOnlySpan<byte> rlp) =>
        SetStorageTrieNodeCalls.Add((address, path, rlp.ToArray()));

    public void SetStorageRawEncoded(in ValueHash256 addrHash, in ValueHash256 slotHash, scoped ReadOnlySpan<byte> rlpValue) =>
        SetStorageRawEncodedCalls.Add((addrHash, slotHash, rlpValue.ToArray()));

    public void SetAccountRaw(in ValueHash256 addrHash, Account account) => SetAccountRawCalls.Add((addrHash, account));

    public void DeleteAccountRange(in ValueHash256 fromPath, in ValueHash256 toPath) =>
        DeleteAccountRangeCalls.Add((fromPath, toPath));

    public void DeleteStorageRange(in ValueHash256 addressHash, in ValueHash256 fromPath, in ValueHash256 toPath) =>
        DeleteStorageRangeCalls.Add((addressHash, fromPath, toPath));

    public void DeleteStateTrieNodeRange(in ValueHash256 from, in ValueHash256 to) =>
        DeleteStateTrieNodeRangeCalls.Add((from, to));

    public void DeleteStorageTrieNodeRange(in ValueHash256 addressHash, in ValueHash256 from, in ValueHash256 to) =>
        DeleteStorageTrieNodeRangeCalls.Add((addressHash, from, to));

    public void Dispose() => DisposeCount++;
}
