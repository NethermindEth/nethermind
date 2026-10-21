// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Flat.NodeBuffer;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.PersistedSnapshots;
using Nethermind.Trie;
using Nethermind.Trie.Native;
using Nethermind.Trie.Pruning;
using NSubstitute;

namespace Nethermind.State.Flat.Test;

public static class FlatTestHelpers
{
    public static Snapshot MakeSnapshot(IResourcePool pool, Action<SnapshotContent>? populate = null)
    {
        SnapshotContent content = pool.GetSnapshotContent(ResourcePool.Usage.MainBlockProcessing);
        populate?.Invoke(content);
        return new Snapshot(StateId.PreGenesis, StateId.PreGenesis, content, pool, ResourcePool.Usage.MainBlockProcessing, null);
    }

    /// <summary>A buffer with 64 KiB generations, so tests can rotate it cheaply.</summary>
    public static INodeRecordBuffer NewTestBuffer() => new NodeRecordBuffer(new NativeNodeBuffer(0, 64 * 1024, NodeRecordBuffer.DefaultLiveGenerations));

    /// <summary>A trie-node path with all nibbles after its length cleared.</summary>
    public static TreePath CanonicalPath(in ValueHash256 hash, int length) => new TreePath(hash, 64).Truncate(length);

    public const string LeafRlpA = "c482200101";
    public const string LeafRlpB = "c482200102";
    public const string LeafRlpC = "c482200103";

    /// <summary>Creates a snapshot owning a retain on the active generation of <paramref name="buffer"/>.</summary>
    public static Snapshot CreateSnapshot(this IResourcePool pool, in StateId from, in StateId to, ResourcePool.Usage usage, INodeRecordBuffer buffer) =>
        new(from, to, pool.GetSnapshotContent(usage), pool, usage, buffer.PinHead());

    /// <summary>Appends records until <paramref name="buffer"/> starts a new generation.</summary>
    public static void StartNewGeneration(INodeRecordBuffer buffer)
    {
        byte[] leaf = Bytes.FromHexString(LeafRlpA);
        ulong generation = AppendNode(buffer, null, TreePath.Empty, leaf) >> 32;
        while (AppendNode(buffer, null, TreePath.Empty, leaf) >> 32 == generation) { }
    }

    /// <summary>Makes a snapshot owning a retain on <paramref name="buffer"/> taken before <paramref name="populate"/> writes its nodes.</summary>
    public static Snapshot MakeSnapshot(IResourcePool pool, INodeRecordBuffer buffer, Action<SnapshotContent> populate)
    {
        Snapshot snapshot = pool.CreateSnapshot(StateId.PreGenesis, new(1, Keccak.Zero), ResourcePool.Usage.MainBlockProcessing, buffer);
        populate(snapshot.Content);
        return snapshot;
    }

    internal static Hash256 WriteNative(INodeRecordBuffer buffer, Hash256? address, Hash256 root, params PatriciaTree.BulkSetEntry[] entries)
    {
        using NativePendingNodes pending = NativePendingNodes.Rent(entries.Length);
        return NativePatriciaBulkSet.BulkSet(new NativeBufferOnlySource(), pending, buffer.Native, null, address, root.ValueHash256, entries);
    }

    internal static FixtureRoot BulkSet(INodeRecordBuffer buffer, Hash256? address, FixtureRoot root, ReadOnlySpan<PatriciaTree.BulkSetEntry> entries)
    {
        using NativePendingNodes pending = NativePendingNodes.Rent(entries.Length);
        Hash256 hash = NativePatriciaBulkSet.BulkSet(new NativeBufferOnlySource(), pending, buffer.Native, null, address, root.Hash, entries);
        return new FixtureRoot(hash.ValueHash256, buffer.FindRoot(address, hash.ValueHash256));
    }

    internal static IEnumerable<(TreePath Path, ulong Pointer, NativeNodeBuffer.FixtureRecord Record)> EnumerateNodes(this INodeRecordBuffer buffer, ulong root) =>
        buffer.EnumerateFixtureNodes(root);

    private sealed class NativeBufferOnlySource : INativeNodeStore
    {
        public bool Fetch(in TreePath path, in ValueHash256 hash, NativeNodeReceiver receiver) => false;
    }

    private static readonly ConditionalWeakTable<SnapshotContent, FixtureNodes> _fixtureNodes = [];

    /// <summary>Connects supplied state nodes to the snapshot root, synthesizing missing ancestors.</summary>
    /// <remarks>Supplied parent RLP is authoritative; inputs it does not reference remain unreachable.</remarks>
    public static ulong SetStateNodeRlp(this SnapshotContent content, INodeRecordBuffer buffer, in TreePath path, string rlpHex) =>
        content.SetStateNodeRlp(buffer, path, Bytes.FromHexString(rlpHex));

    /// <inheritdoc cref="SetStateNodeRlp(SnapshotContent, INodeRecordBuffer, in TreePath, string)"/>
    public static ulong SetStateNodeRlp(this SnapshotContent content, INodeRecordBuffer buffer, in TreePath path, byte[] rlp)
    {
        FixtureTrie trie = _fixtureNodes.GetValue(content, static _ => new FixtureNodes()).State;
        if (content.StateRootPointer != trie.RootPointer) trie.Nodes.Clear();
        ulong pointer = trie.Set(buffer, null, path, rlp);
        content.StateRootPointer = trie.RootPointer;
        return pointer;
    }

    /// <summary>Connects the supplied storage node to its address's root, synthesizing missing ancestors.</summary>
    public static ulong SetStorageNodeRlp(this SnapshotContent content, INodeRecordBuffer buffer, Hash256 address, in TreePath path, string rlpHex) =>
        content.SetStorageNodeRlp(buffer, address, path, Bytes.FromHexString(rlpHex));

    /// <inheritdoc cref="SetStorageNodeRlp(SnapshotContent, INodeRecordBuffer, Hash256, in TreePath, string)"/>
    public static ulong SetStorageNodeRlp(this SnapshotContent content, INodeRecordBuffer buffer, Hash256 address, in TreePath path, byte[] rlp)
    {
        FixtureNodes fixtures = _fixtureNodes.GetValue(content, static _ => new FixtureNodes());
        if (!fixtures.Storage.TryGetValue(address, out FixtureTrie? trie)) fixtures.Storage[address] = trie = new();
        if (!content.StorageRoots.TryGetValue(address, out ulong root) || root != trie.RootPointer) trie.Nodes.Clear();
        ulong pointer = trie.Set(buffer, address, path, rlp);
        content.StorageRoots[address] = trie.RootPointer;
        return pointer;
    }

    private sealed class FixtureNodes
    {
        public readonly FixtureTrie State = new();
        public readonly Dictionary<Hash256, FixtureTrie> Storage = [];
    }

    private sealed class FixtureTrie
    {
        public readonly Dictionary<TreePath, byte[]> Nodes = [];
        public ulong RootPointer;

        public ulong Set(INodeRecordBuffer buffer, Hash256? address, in TreePath path, byte[] rlp)
        {
            TreePath normalizedPath = CanonicalPath(path.Path, path.Length);
            Nodes[normalizedPath] = rlp;
            RootPointer = Build(buffer, address, TreePath.Empty, Nodes.ToArray()).Pointer;
            return buffer.TryGetNodePointer(RootPointer, path, out ulong pointer) ? pointer : AppendNode(buffer, address, path, rlp);
        }

        private static FixtureRoot Build(INodeRecordBuffer buffer, Hash256? address, TreePath path, KeyValuePair<TreePath, byte[]>[] nodes)
        {
            KeyValuePair<TreePath, byte[]> exact = nodes.FirstOrDefault(n => n.Key == path);
            byte[] rlp;
            FixtureRoot[] references = new FixtureRoot[16];
            if (exact.Value is not null)
            {
                rlp = exact.Value;
            }
            else
            {
                int shared = path.Length;
                while (shared < nodes[0].Key.Length && nodes.All(n => n.Key.Length > shared && n.Key[shared] == nodes[0].Key[shared])) shared++;
                if (shared > path.Length)
                {
                    TreePath childPath = nodes[0].Key.Truncate(shared);
                    references[0] = Build(buffer, address, childPath, nodes);
                    byte[] key = Enumerable.Range(path.Length, shared - path.Length).Select(i => (byte)nodes[0].Key[i]).ToArray();
                    rlp = ExtensionRlp(key, references[0].Hash);
                }
                else
                {
                    List<(int Index, ValueHash256 Hash)> children = [];
                    for (int i = 0; i < references.Length; i++)
                    {
                        TreePath childPath = path.Append(i);
                        KeyValuePair<TreePath, byte[]>[] descendants = nodes.Where(n => n.Key.Length >= childPath.Length && n.Key.StartsWith(childPath)).ToArray();
                        if (descendants.Length == 0) continue;
                        references[i] = Build(buffer, address, childPath, descendants);
                        children.Add((i, references[i].Hash));
                    }
                    rlp = BranchRlp([.. children]);
                }
            }

            TrieNode node = new(NodeType.Unknown, rlp);
            node.ResolveNode(NullTrieNodeResolver.Instance, path);
            ulong[] pointers = new ulong[16];
            if (!node.IsLeaf)
            {
                int count = node.IsExtension ? 1 : 16;
                for (int i = 0; i < count; i++)
                {
                    Hash256? expectedHash = node.GetChildHash(i);
                    if (expectedHash is null) continue;
                    TreePath childPath = node.IsExtension ? path.Append(node.Key!) : path.Append(i);
                    FixtureRoot child = references[i];
                    if (exact.Value is not null)
                    {
                        KeyValuePair<TreePath, byte[]>[] descendants = nodes.Where(n => n.Key.Length >= childPath.Length && n.Key.StartsWith(childPath)).ToArray();
                        if (descendants.Length > 0) child = Build(buffer, address, childPath, descendants);
                    }
                    if (child.Pointer != 0 && child.Hash == expectedHash.ValueHash256) pointers[i] = child.Pointer;
                }
            }

            ulong pointer = buffer.Native.AppendRlp(address, path, rlp, pointers, indexRoot: path.Length == 0);
            return new FixtureRoot(ValueKeccak.Compute(rlp), pointer);
        }
    }

    /// <summary>The RLP of a leaf with an empty key holding <paramref name="value"/>, a distinct valid trie node per value.</summary>
    public static byte[] LeafRlp(params byte[] value) => Rlp.Encode(Rlp.Encode(HexPrefix.ToBytes([], isLeaf: true)), Rlp.Encode(value)).Bytes;

    internal static byte[] ExtensionRlp(byte[] key, in ValueHash256 childHash) =>
        Rlp.Encode(Rlp.Encode(HexPrefix.ToBytes(key, isLeaf: false)), Rlp.Encode(new Hash256(childHash))).Bytes;

    internal static byte[] BranchRlp(params (int Index, ValueHash256 Hash)[] children)
    {
        Rlp[] items = [.. Enumerable.Repeat(Rlp.OfEmptyByteArray, 17)];
        foreach ((int index, ValueHash256 hash) in children) items[index] = Rlp.Encode(new Hash256(hash));
        return Rlp.Encode(items).Bytes;
    }

    private static ulong AppendNode(INodeRecordBuffer buffer, Hash256? address, in TreePath path, byte[] rlp) =>
        buffer.Native.AppendRlp(address, path, rlp, indexRoot: path.Length == 0);

    internal static ulong AppendRecordFromRlp(this INodeRecordBuffer buffer, Hash256? address, in TreePath path, in ValueHash256 hash, ReadOnlySpan<byte> rlp)
    {
        if (ValueKeccak.Compute(rlp) != hash) throw new ArgumentException("Fixture node hash does not match its RLP.", nameof(hash));
        return buffer.Native.AppendRlp(address, path, rlp, indexRoot: path.Length == 0);
    }

    /// <summary>The pointer of the state node at <paramref name="key"/> written within the snapshot's range.</summary>
    internal static bool TryGetStateNode(this Snapshot snapshot, HashedKey<TreePath> key, out ulong pointer) =>
        snapshot.TryGetNodePointer(snapshot.StateRootPointer, key.Key, out pointer);

    /// <summary>The pointer of the storage node at <paramref name="key"/> written within the snapshot's range.</summary>
    internal static bool TryGetStorageNode(this Snapshot snapshot, HashedKey<(Hash256, TreePath)> key, out ulong pointer)
    {
        pointer = 0;
        return snapshot.TryGetStorageRoot(key.Key.Item1, out ulong root) && snapshot.TryGetNodePointer(root, key.Key.Item2, out pointer);
    }

    private static bool TryGetNodePointer(this Snapshot snapshot, ulong root, in TreePath path, out ulong pointer)
    {
        pointer = 0;
        if (snapshot.BufferPin is not { } pin) return false;
        foreach ((TreePath nodePath, ulong candidate) in pin.Buffer.EnumerateNodes(root, pin.WriteBoundary))
        {
            if (nodePath == path)
            {
                pointer = candidate;
                return true;
            }
        }
        return false;
    }

    internal static bool TryGetNodePointer(this INodeRecordBuffer buffer, ulong root, in TreePath path, out ulong pointer)
    {
        foreach ((TreePath nodePath, ulong candidate, _) in buffer.EnumerateFixtureNodes(root))
        {
            if (nodePath == path)
            {
                pointer = candidate;
                return true;
            }
        }
        pointer = 0;
        return false;
    }

    internal static IEnumerable<(TreePath Path, ulong Pointer, NativeNodeBuffer.FixtureRecord Record)> EnumerateFixtureNodes(this INodeRecordBuffer buffer, ulong root)
    {
        if (root == 0) yield break;
        Stack<(TreePath Path, ulong Pointer)> pending = new();
        pending.Push((TreePath.Empty, root));
        while (pending.TryPop(out (TreePath Path, ulong Pointer) next))
        {
            NativeNodeBuffer.FixtureRecord record = buffer.Native.GetRecord(next.Pointer);
            yield return (next.Path, next.Pointer, record);
            TrieNode node = new(NodeType.Unknown, record.Rlp.ToArray());
            node.ResolveNode(NullTrieNodeResolver.Instance, next.Path);
            if (node.IsLeaf) continue;
            if (node.IsExtension)
            {
                ulong pointer = record.ChildPointer(0);
                if (pointer != 0) pending.Push((next.Path.Append(node.Key!), pointer));
            }
            else
            {
                for (int i = 15; i >= 0; i--)
                {
                    ulong pointer = record.ChildPointer(i);
                    if (pointer != 0) pending.Push((next.Path.Append(i), pointer));
                }
            }
        }
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
    /// <param name="buffer">The buffer <paramref name="populate"/> records nodes in; the snapshot retains it.</param>
    public static ReadOnlySnapshotBundle MakeBundle(ResourcePool pool, INodeRecordBuffer buffer, Action<SnapshotContent> populate) =>
        new(SnapshotList(MakeSnapshot(pool, buffer, populate)), Substitute.For<IPersistence.IPersistenceReader>(),
            recordDetailedMetrics: false, PersistedSnapshotStack.Empty());
}

/// <summary>
/// A persistence reader to create with <c>Substitute.For</c> where the code under test reads trie node RLP through the span
/// reads: Castle DynamicProxy (NSubstitute's backing) throws <c>InvalidProgramException</c> when proxying them over a
/// ref-struct argument, so they forward to the substitutable array reads instead.
/// </summary>
public abstract class SubstitutablePersistenceReader : IPersistence.IPersistenceReader
{
    public abstract Account? GetAccount(Address address);
    public abstract bool TryGetSlot(Address address, in UInt256 slot, ref UInt256 outValue);
    public abstract StateId CurrentState { get; }
    public abstract byte[]? TryLoadStateRlp(in TreePath path, ReadFlags flags);
    public abstract byte[]? TryLoadStorageRlp(Hash256 address, in TreePath path, ReadFlags flags);
    public abstract byte[]? GetAccountRaw(in ValueHash256 addrHash);
    public abstract bool TryGetStorageRaw(in ValueHash256 addrHash, in ValueHash256 slotHash, ref UInt256 value);
    public abstract IPersistence.IFlatIterator CreateAccountIterator(in ValueHash256 startKey, in ValueHash256 endKey);
    public abstract IPersistence.IFlatIterator CreateStorageIterator(in ValueHash256 accountKey, in ValueHash256 startSlotKey, in ValueHash256 endSlotKey);
    public abstract bool IsPreimageMode { get; }
    public abstract void Dispose();

    bool IPersistence.IPersistenceReader.TryLoadStateRlp<TArg>(in TreePath path, ReadFlags flags, ReadOnlySpanAction<byte, TArg> action, TArg arg) =>
        Pass(TryLoadStateRlp(path, flags), action, arg);

    bool IPersistence.IPersistenceReader.TryLoadStorageRlp<TArg>(Hash256 address, in TreePath path, ReadFlags flags, ReadOnlySpanAction<byte, TArg> action, TArg arg) =>
        Pass(TryLoadStorageRlp(address, path, flags), action, arg);

    private static bool Pass<TArg>(byte[]? rlp, ReadOnlySpanAction<byte, TArg> action, TArg arg) where TArg : allows ref struct
    {
        if (rlp is null) return false;
        action(rlp, arg);
        return true;
    }
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

internal readonly record struct FixtureRoot(ValueHash256 Hash, ulong Pointer)
{
    internal static FixtureRoot Empty => new(ValueKeccak.EmptyTreeHash, 0);

    internal Hash256 ToHash256() => new(Hash);
}
