// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.Rust.Interop;
using Nethermind.Trie;
using NUnit.Framework;
using BlockPlan = Nethermind.State.Flat.Rust.Test.RustFlatDbManagerDifferentialTests.BlockPlan;
using static Nethermind.State.Flat.Rust.Interop.OpFieldWriter;

namespace Nethermind.State.Flat.Rust.Test;

/// <summary>
/// The snapshot manager of a test container as the tests drive it: the original <see cref="IFlatDbManager"/> on the C#
/// snapshot backends, the forked one on <see cref="Nethermind.Db.FlatDbBackend.Rust"/>, whose bundles and snapshots are
/// those of the forked package.
/// </summary>
internal abstract class FlatArm
{
    /// <summary>Builds the snapshot of <paramref name="plan"/> from <paramref name="from"/> to <paramref name="to"/> and adds it.</summary>
    /// <returns>The trie nodes of the plan, its state nodes then its storage nodes: one with RLP is a leaf, which is
    /// not persisted yet, one without is a hash-only placeholder.</returns>
    public TrieNode[] Add(BlockPlan plan, in StateId from, in StateId to) => Add(plan, from, to, out _);

    /// <inheritdoc cref="Add(BlockPlan, in StateId, in StateId)"/>
    /// <param name="content">The content of the snapshot before it was added, as the records of
    /// native/crates/flatdb-ffi/src/snapshot_stream.rs, every kind sorted as the C# comparers sort it.</param>
    public abstract TrieNode[] Add(BlockPlan plan, in StateId from, in StateId to, out byte[] content);

    /// <summary>The read-only bundle of <paramref name="state"/>.</summary>
    public abstract IBundleView GatherReadOnly(in StateId state, ReaderFlags readerFlags);

    /// <summary>A scope bundle on <paramref name="state"/>, which reads as the read-only bundle it is gathered over.</summary>
    public abstract IBundleView GatherScope(in StateId state, ResourcePool.Usage usage, bool filterInMemorySlotReads);

    public abstract bool HasStateForBlock(in StateId state);

    public abstract void FlushCache(CancellationToken cancellationToken);

    public abstract void DropStateNotReachableFrom(in StateId head);

    /// <summary>The trie nodes of <paramref name="plan"/>, as <see cref="Add(BlockPlan, in StateId, in StateId)"/> returns them.</summary>
    protected static (TreePath Path, Hash256? Owner, TrieNode Node)[] Nodes(BlockPlan plan) =>
    [
        .. plan.StateNodes.Select(static node => (node.Path, (Hash256?)null, Node(node.Hash, node.Rlp))),
        .. plan.StorageNodes.Select(static node => (node.Path, (Hash256?)node.Owner, Node(node.Hash, node.Rlp))),
    ];

    private static TrieNode Node(Hash256 hash, byte[]? rlp) => rlp is null ? new TrieNode(NodeType.Unknown, hash) : new TrieNode(NodeType.Leaf, hash, rlp);

    /// <summary>The original manager, whose snapshots hold C# content.</summary>
    internal sealed class CSharp(IFlatDbManager manager, IResourcePool resourcePool) : FlatArm
    {
        public override TrieNode[] Add(BlockPlan plan, in StateId from, in StateId to, out byte[] content)
        {
            Snapshot snapshot = Build(resourcePool, plan, from, to, ResourcePool.Usage.MainBlockProcessing, out TrieNode[] nodes);
            content = SnapshotRecords.Sorted(snapshot.Content);
            manager.AddSnapshot(snapshot, resourcePool.GetCachedResource(ResourcePool.Usage.MainBlockProcessing));
            return nodes;
        }

        /// <summary>A C# snapshot holding the writes of <paramref name="plan"/>, and the trie nodes it sets.</summary>
        public static Snapshot Build(IResourcePool resourcePool, BlockPlan plan, in StateId from, in StateId to, ResourcePool.Usage usage, out TrieNode[] nodes)
        {
            Snapshot snapshot = resourcePool.CreateSnapshot(from, to, usage);
            SnapshotContent written = snapshot.Content;
            foreach ((Address address, bool isNew) in plan.SelfDestructs) written.SelfDestructedStorageAddresses[address] = isNew;
            foreach ((Address address, Account? account) in plan.Accounts) written.Accounts[address] = account;
            foreach ((Address address, UInt256 slot, UInt256? value) in plan.Slots) written.Storages[(address, slot)] = value;

            (TreePath Path, Hash256? Owner, TrieNode Node)[] planned = Nodes(plan);
            foreach ((TreePath path, Hash256? owner, TrieNode node) in planned)
            {
                if (owner is null) written.StateNodes[path] = node;
                else written.StorageNodes[(owner, path)] = node;
            }

            nodes = [.. planned.Select(static node => node.Node)];
            return snapshot;
        }

        public override IBundleView GatherReadOnly(in StateId state, ReaderFlags readerFlags) =>
            new ReadOnlyView(manager.GatherReadOnlySnapshotBundle(state, readerFlags));

        public override IBundleView GatherScope(in StateId state, ResourcePool.Usage usage, bool filterInMemorySlotReads) =>
            new ScopeView(manager.GatherSnapshotBundle(state, usage, filterInMemorySlotReads));

        public override bool HasStateForBlock(in StateId state) => manager.HasStateForBlock(state);

        public override void FlushCache(CancellationToken cancellationToken) => manager.FlushCache(cancellationToken);

        public override void DropStateNotReachableFrom(in StateId head) => manager.DropStateNotReachableFrom(head);

        internal sealed class ReadOnlyView(ReadOnlySnapshotBundle bundle) : IBundleView
        {
            public Account? GetAccount(Address address) => bundle.GetAccount(address);

            public int DetermineSelfDestructSnapshotIdx(Address address) => bundle.DetermineSelfDestructSnapshotIdx(address);

            public UInt256? GetSlot(Address address, in UInt256 slot, int selfDestructIdx)
            {
                bundle.GetSlot(address, slot, selfDestructIdx, out UInt256? value);
                return value;
            }

            public byte[]? LoadStateRlp(in TreePath path) =>
                bundle.TryFindStateNodes(path, Keccak.Zero, out TrieNode? node) && !IsPlaceholder(node)
                    ? node.FullRlp.ToArray()
                    : bundle.TryLoadStateRlp(path, Keccak.Zero, ReadFlags.None);

            public byte[]? LoadStorageRlp(Hash256 owner, in TreePath path) =>
                bundle.TryFindStorageNodes(owner, path, Keccak.Zero, out TrieNode? node) && !IsPlaceholder(node)
                    ? node.FullRlp.ToArray()
                    : bundle.TryLoadStorageRlp(owner, path, Keccak.Zero, ReadFlags.None);

            public void Dispose() => bundle.Dispose();

            private static bool IsPlaceholder(TrieNode node) => node.NodeType == NodeType.Unknown && node.FullRlp.Length == 0;
        }

        private sealed class ScopeView(SnapshotBundle bundle) : IBundleView
        {
            public Account? GetAccount(Address address) => bundle.GetAccount(address);

            public int DetermineSelfDestructSnapshotIdx(Address address) => bundle.DetermineSelfDestructSnapshotIdx(address);

            public UInt256? GetSlot(Address address, in UInt256 slot, int selfDestructIdx)
            {
                bundle.GetSlot(address, slot, selfDestructIdx, out UInt256? value);
                return value;
            }

            public byte[]? LoadStateRlp(in TreePath path) => bundle.TryLoadStateRlp(path, Keccak.Zero, ReadFlags.None);

            public byte[]? LoadStorageRlp(Hash256 owner, in TreePath path) => bundle.TryLoadStorageRlp(owner, path, Keccak.Zero, ReadFlags.None);

            public void Dispose() => bundle.Dispose();
        }
    }

    /// <summary>The forked manager, whose snapshots are native; the tests build them from records, with the nodes the plan sets.</summary>
    internal sealed class Rust(Fork.IFlatDbManager manager, IResourcePool resourcePool) : FlatArm
    {
        public override TrieNode[] Add(BlockPlan plan, in StateId from, in StateId to, out byte[] content)
        {
            Fork.Snapshot snapshot = Build(plan, from, to, out TrieNode[] nodes);
            content = NativeTestSnapshots.Dump(snapshot);
            manager.AddSnapshot(snapshot, resourcePool.GetCachedResource(ResourcePool.Usage.MainBlockProcessing));
            return nodes;
        }

        /// <summary>A native snapshot holding the writes of <paramref name="plan"/>, with the trie nodes it sets as a committing bundle captures them.</summary>
        public static Fork.Snapshot Build(BlockPlan plan, in StateId from, in StateId to, out TrieNode[] nodes)
        {
            (TreePath Path, Hash256? Owner, TrieNode Node)[] planned = Nodes(plan);
            Fork.NodeCaptures captures = new();
            foreach ((TreePath path, Hash256? owner, TrieNode node) in planned)
            {
                if (node.FullRlp.Length == 0) continue;
                if (owner is null) captures.AddState(path, node);
                else captures.AddStorage(owner, path, node);
            }

            nodes = [.. planned.Select(static node => node.Node)];
            return new Fork.Snapshot(from, to, NativeTestSnapshots.FromRecords(from, to, SnapshotRecords.Of(plan)), captures.Take());
        }

        public override IBundleView GatherReadOnly(in StateId state, ReaderFlags readerFlags) =>
            new ReadOnlyView(manager.GatherReadOnlySnapshotBundle(state, readerFlags));

        public override IBundleView GatherScope(in StateId state, ResourcePool.Usage usage, bool filterInMemorySlotReads) =>
            new ScopeView(manager.GatherSnapshotBundle(state, usage, filterInMemorySlotReads));

        public override bool HasStateForBlock(in StateId state) => manager.HasStateForBlock(state);

        public override void FlushCache(CancellationToken cancellationToken) => manager.FlushCache(cancellationToken);

        public override void DropStateNotReachableFrom(in StateId head) => manager.DropStateNotReachableFrom(head);

        /// <remarks>The fork's read-only bundle finds no node object, so every node is loaded, unchecked, as RLP.</remarks>
        private sealed class ReadOnlyView(Fork.ReadOnlySnapshotBundle bundle) : IBundleView
        {
            public Account? GetAccount(Address address) => bundle.GetAccount(address);

            public int DetermineSelfDestructSnapshotIdx(Address address) => bundle.DetermineSelfDestructSnapshotIdx(address);

            public UInt256? GetSlot(Address address, in UInt256 slot, int selfDestructIdx)
            {
                bundle.GetSlot(address, slot, selfDestructIdx, out UInt256? value);
                return value;
            }

            public byte[]? LoadStateRlp(in TreePath path) =>
                bundle.TryFindStateNodes(path, Keccak.Zero, out TrieNode? node) ? node.FullRlp.ToArray() : bundle.TryLoadStateRlp(path, Keccak.Zero, ReadFlags.None);

            public byte[]? LoadStorageRlp(Hash256 owner, in TreePath path) =>
                bundle.TryFindStorageNodes(owner, path, Keccak.Zero, out TrieNode? node) ? node.FullRlp.ToArray() : bundle.TryLoadStorageRlp(owner, path, Keccak.Zero, ReadFlags.None);

            public void Dispose() => bundle.Dispose();
        }

        private sealed class ScopeView(Fork.SnapshotBundle bundle) : IBundleView
        {
            public Account? GetAccount(Address address) => bundle.GetAccount(address);

            public int DetermineSelfDestructSnapshotIdx(Address address) => bundle.DetermineSelfDestructSnapshotIdx(address);

            public UInt256? GetSlot(Address address, in UInt256 slot, int selfDestructIdx)
            {
                bundle.GetSlot(address, slot, selfDestructIdx, out UInt256? value);
                return value;
            }

            public byte[]? LoadStateRlp(in TreePath path) => bundle.TryLoadStateRlp(path, Keccak.Zero, ReadFlags.None);

            public byte[]? LoadStorageRlp(Hash256 owner, in TreePath path) => bundle.TryLoadStorageRlp(owner, path, Keccak.Zero, ReadFlags.None);

            public void Dispose() => bundle.Dispose();
        }
    }
}

/// <summary>The reads of a read-only bundle or a scope bundle of either package.</summary>
internal interface IBundleView : IDisposable
{
    Account? GetAccount(Address address);

    int DetermineSelfDestructSnapshotIdx(Address address);

    UInt256? GetSlot(Address address, in UInt256 slot, int selfDestructIdx);

    /// <summary>The state trie node at <paramref name="path"/> whatever its hash, as the read-only trie store reads it.</summary>
    byte[]? LoadStateRlp(in TreePath path);

    /// <inheritdoc cref="LoadStateRlp"/>
    byte[]? LoadStorageRlp(Hash256 owner, in TreePath path);
}

/// <summary>The record stream of native/crates/flatdb-ffi/src/snapshot_stream.rs, which the test exports read and write.</summary>
internal static class SnapshotRecords
{
    private const byte SetAccount = 0;
    private const byte DeleteAccount = 1;
    private const byte SetSlot = 2;
    private const byte DeleteSlot = 3;
    private const byte SelfDestruct = 4;
    private const byte StateNode = 5;
    private const byte StorageNode = 6;

    /// <summary>The writes of <paramref name="plan"/>, in its order; a later write of a key replaces the earlier one.</summary>
    public static byte[] Of(BlockPlan plan)
    {
        ArrayBufferWriter<byte> records = new();
        foreach ((Address address, bool isNew) in plan.SelfDestructs) WriteSelfDestruct(records, address, isNew);
        foreach ((Address address, Account? account) in plan.Accounts) WriteAccount(records, address, account);
        foreach ((Address address, UInt256 slot, UInt256? value) in plan.Slots) WriteSlot(records, address, slot, value);
        foreach ((TreePath path, Hash256 hash, byte[]? rlp) in plan.StateNodes) WriteStateNode(records, path, hash, rlp ?? []);
        foreach ((Hash256 owner, TreePath path, Hash256 hash, byte[]? rlp) in plan.StorageNodes) WriteStorageNode(records, owner, path, hash, rlp ?? []);
        return records.WrittenSpan.ToArray();
    }

    /// <summary>The content of a C# snapshot as <c>fdb_test_snapshot_dump</c> dumps a native one: each kind sorted as its C# comparer sorts it.</summary>
    public static byte[] Sorted(SnapshotContent content)
    {
        ArrayBufferWriter<byte> records = new();
        foreach (KeyValuePair<HashedKey<Address>, Account?> account in content.Accounts.OrderBy(static entry => entry.Key.Key.Bytes.ToArray(), Bytes.Comparer))
        {
            WriteAccount(records, account.Key.Key, account.Value);
        }

        foreach (KeyValuePair<HashedKey<Address>, bool> clear in content.SelfDestructedStorageAddresses.OrderBy(static entry => entry.Key.Key.Bytes.ToArray(), Bytes.Comparer))
        {
            WriteSelfDestruct(records, clear.Key.Key, clear.Value);
        }

        foreach (KeyValuePair<HashedKey<(Address, UInt256)>, UInt256?> slot in content.Storages
                     .OrderBy(static entry => entry.Key.Key.Item1.Bytes.ToArray(), Bytes.Comparer).ThenBy(static entry => entry.Key.Key.Item2))
        {
            WriteSlot(records, slot.Key.Key.Item1, slot.Key.Key.Item2, slot.Value);
        }

        foreach (KeyValuePair<HashedKey<TreePath>, TrieNode> node in content.StateNodes.OrderBy(static entry => entry.Key.Key))
        {
            WriteStateNode(records, node.Key.Key, node.Value.Keccak, node.Value.FullRlp.AsSpan());
        }

        foreach (KeyValuePair<HashedKey<(Hash256, TreePath)>, TrieNode> node in content.StorageNodes
                     .OrderBy(static entry => entry.Key.Key.Item1.Bytes.ToArray(), Bytes.Comparer).ThenBy(static entry => entry.Key.Key.Item2))
        {
            WriteStorageNode(records, node.Key.Key.Item1, node.Key.Key.Item2, node.Value.Keccak, node.Value.FullRlp.AsSpan());
        }

        return records.WrittenSpan.ToArray();
    }

    private static void WriteAccount(ArrayBufferWriter<byte> records, Address address, Account? account)
    {
        if (account is null)
        {
            Record(records, DeleteAccount, AddressSize).Write(address.Bytes);
            return;
        }

        OpFieldWriter record = Record(records, SetAccount, AddressSize + AccountSize);
        record.Write(address.Bytes);
        record.Write(account);
    }

    private static void WriteSelfDestruct(ArrayBufferWriter<byte> records, Address address, bool isNew)
    {
        OpFieldWriter record = Record(records, SelfDestruct, AddressSize + 1);
        record.Write(address.Bytes);
        record.Write(isNew ? (byte)1 : (byte)0);
    }

    private static void WriteSlot(ArrayBufferWriter<byte> records, Address address, in UInt256 slot, UInt256? value)
    {
        OpFieldWriter record = Record(records, value is null ? DeleteSlot : SetSlot, AddressSize + (value is null ? 1 : 2) * UInt256Size);
        record.Write(address.Bytes);
        record.Write(slot);
        if (value is { } written) record.Write(written);
    }

    /// <param name="hash">The node's hash; null, for an inline node, is written as zero.</param>
    private static void WriteStateNode(ArrayBufferWriter<byte> records, in TreePath path, Hash256? hash, ReadOnlySpan<byte> rlp)
    {
        OpFieldWriter record = Record(records, StateNode, PathSize + HashSize + LengthSize + rlp.Length);
        record.Write(path);
        record.Write((hash ?? Keccak.Zero).Bytes);
        record.WriteWithLength(rlp);
    }

    /// <inheritdoc cref="WriteStateNode"/>
    private static void WriteStorageNode(ArrayBufferWriter<byte> records, Hash256 owner, in TreePath path, Hash256? hash, ReadOnlySpan<byte> rlp)
    {
        OpFieldWriter record = Record(records, StorageNode, HashSize + PathSize + HashSize + LengthSize + rlp.Length);
        record.Write(owner.Bytes);
        record.Write(path);
        record.Write((hash ?? Keccak.Zero).Bytes);
        record.WriteWithLength(rlp);
    }

    private static OpFieldWriter Record(ArrayBufferWriter<byte> records, byte tag, int size)
    {
        Span<byte> record = records.GetSpan(1 + size)[..(1 + size)];
        records.Advance(1 + size);
        record[0] = tag;
        return new OpFieldWriter(record[1..]);
    }
}

/// <summary>The snapshot exports of the library's test-api build, which the production library does not have.</summary>
internal static unsafe partial class NativeTestSnapshots
{
    private static readonly Lazy<bool> _isAvailable = new(static () =>
        NativeMethods.IsAvailable
        && NativeLibrary.TryLoad("nethermind_flatdb", typeof(NativeMethods).Assembly, null, out nint library)
        && NativeLibrary.TryGetExport(library, nameof(fdb_test_snapshot_from_records), out _));

    /// <summary>Ignores the calling test unless the library was built with its test-api feature.</summary>
    public static void Require()
    {
        RustNativeLibrary.Require();
        if (!_isAvailable.Value) Assert.Ignore("libnethermind_flatdb.so was built without the test-api feature");
    }

    /// <summary>A native snapshot from <paramref name="from"/> to <paramref name="to"/> holding the writes of <paramref name="records"/>.</summary>
    public static SnapshotHandle FromRecords(in StateId from, in StateId to, ReadOnlySpan<byte> records)
    {
        Require();
        FfiStateId nativeFrom = new(from);
        FfiStateId nativeTo = new(to);
        nint snapshot;
        fixed (byte* data = records)
        {
            StatusMap.ThrowIfFailed(fdb_test_snapshot_from_records(&nativeFrom, &nativeTo, data, (nuint)records.Length, &snapshot));
        }

        return new SnapshotHandle(snapshot);
    }

    /// <summary>The content of <paramref name="snapshot"/>, every kind sorted as the C# comparers sort it.</summary>
    public static byte[] Dump(Fork.Snapshot snapshot)
    {
        ulong length = 0;
        FdbStatus status = fdb_test_snapshot_dump(snapshot.Handle.DangerousGetHandle(), null, 0, &length);
        if (status != FdbStatus.BufferTooSmall) StatusMap.ThrowIfFailed(status);
        byte[] dump = new byte[length];
        fixed (byte* data = dump)
        {
            StatusMap.ThrowIfFailed(fdb_test_snapshot_dump(snapshot.Handle.DangerousGetHandle(), data, length, &length));
        }

        return dump;
    }

    [LibraryImport("nethermind_flatdb")]
    private static partial FdbStatus fdb_test_snapshot_from_records(FfiStateId* from, FfiStateId* to, byte* records, nuint length, nint* snapshot);

    [LibraryImport("nethermind_flatdb")]
    private static partial FdbStatus fdb_test_snapshot_dump(nint snapshot, byte* buffer, ulong capacity, ulong* length);
}
