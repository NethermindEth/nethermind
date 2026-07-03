// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.PersistedSnapshots;
using Nethermind.Trie;
using NSubstitute;

namespace Nethermind.State.Flat.Test;

internal static class FlatTestHelpers
{
    public static Snapshot MakeSnapshot(IResourcePool pool, Action<SnapshotContent>? populate = null)
    {
        SnapshotContent content = pool.GetSnapshotContent(ResourcePool.Usage.MainBlockProcessing);
        populate?.Invoke(content);
        return new Snapshot(StateId.PreGenesis, StateId.PreGenesis, content, pool, ResourcePool.Usage.MainBlockProcessing);
    }

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
        new(SnapshotList(MakeSnapshot(pool, populate)), Substitute.For<IPersistence.IPersistenceReader>(),
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
    public List<(Address Addr, UInt256 Slot, SlotValue? Value)> SetStorageCalls { get; } = [];
    public List<(TreePath Path, byte[] Rlp)> SetStateTrieNodeCalls { get; } = [];
    public List<(Hash256 Address, TreePath Path, byte[] Rlp)> SetStorageTrieNodeCalls { get; } = [];
    public List<(ValueHash256 AddrHash, ValueHash256 SlotHash, byte[] RlpValue)> SetStorageRawEncodedCalls { get; } = [];
    public List<(ValueHash256 AddrHash, Account Account)> SetAccountRawCalls { get; } = [];
    public List<(ValueHash256 FromPath, ValueHash256 ToPath)> DeleteAccountRangeCalls { get; } = [];
    public List<(ValueHash256 AddressHash, ValueHash256 FromPath, ValueHash256 ToPath)> DeleteStorageRangeCalls { get; } = [];
    public List<(TreePath FromPath, TreePath ToPath)> DeleteStateTrieNodeRangeCalls { get; } = [];
    public List<(ValueHash256 AddressHash, TreePath FromPath, TreePath ToPath)> DeleteStorageTrieNodeRangeCalls { get; } = [];
    public int DisposeCount { get; private set; }

    public void SelfDestruct(Address addr) => SelfDestructCalls.Add(addr);
    public void SetAccount(Address addr, Account? account) => SetAccountCalls.Add((addr, account));
    public void SetStorage(Address addr, in UInt256 slot, in SlotValue? value) => SetStorageCalls.Add((addr, slot, value));

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

    public void DeleteStateTrieNodeRange(in TreePath fromPath, in TreePath toPath) =>
        DeleteStateTrieNodeRangeCalls.Add((fromPath, toPath));

    public void DeleteStorageTrieNodeRange(in ValueHash256 addressHash, in TreePath fromPath, in TreePath toPath) =>
        DeleteStorageTrieNodeRangeCalls.Add((addressHash, fromPath, toPath));

    public void Dispose() => DisposeCount++;
}
