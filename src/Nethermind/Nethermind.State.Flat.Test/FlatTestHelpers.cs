// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
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
        return new Snapshot(StateId.PreGenesis, StateId.PreGenesis, content, pool, ResourcePool.Usage.MainBlockProcessing);
    }

    /// <summary>A log with 64 KiB generations, so tests can rotate it cheaply.</summary>
    public static NativeTrieNodeLog NewTestLog() => new(0, 64 * 1024);

    /// <summary>A trie-node path with all nibbles after its length cleared.</summary>
    public static TreePath CanonicalPath(in ValueHash256 hash, int length) => new TreePath(hash, 64).Truncate(length);

    public const string LeafRlpA = "c482200101";
    public const string LeafRlpB = "c482200102";
    public const string LeafRlpC = "c482200103";

    /// <summary>Creates a snapshot owning a retain on the active generation of <paramref name="log"/>.</summary>
    public static Snapshot CreateSnapshot(this IResourcePool pool, in StateId from, in StateId to, ResourcePool.Usage usage, NativeTrieNodeLog log) =>
        new(from, to, pool.GetSnapshotContent(usage), pool, usage) { LogPin = log.PinActiveGeneration() };

    /// <summary>Appends records until <paramref name="log"/> starts a new generation.</summary>
    public static void StartNewGeneration(NativeTrieNodeLog log)
    {
        byte[] leaf = Bytes.FromHexString(LeafRlpA);
        ulong generation = AppendNode(log, null, TreePath.Empty, leaf) >> 32;
        while (AppendNode(log, null, TreePath.Empty, leaf) >> 32 == generation) { }
    }

    /// <summary>Makes a snapshot owning a retain on <paramref name="log"/> taken before <paramref name="populate"/> writes its nodes.</summary>
    public static Snapshot MakeSnapshot(IResourcePool pool, NativeTrieNodeLog log, Action<SnapshotContent> populate)
    {
        Snapshot snapshot = pool.CreateSnapshot(StateId.PreGenesis, new(1, Keccak.Zero), ResourcePool.Usage.MainBlockProcessing, log);
        populate(snapshot.Content);
        return snapshot;
    }

    internal static Hash256 WriteNative(NativeTrieNodeLog log, Hash256? address, Hash256 root, params PatriciaTree.BulkSetEntry[] entries)
    {
        using NativePendingNodes pending = NativePendingNodes.Rent(entries.Length);
        return NativePatriciaBulkSet.BulkSet(new NativeLogOnlySource(), pending, log, null, address, root.ValueHash256, entries);
    }

    internal static FixtureRoot BulkSet(NativeTrieNodeLog log, Hash256? address, FixtureRoot root, ReadOnlySpan<PatriciaTree.BulkSetEntry> entries)
    {
        using NativePendingNodes pending = NativePendingNodes.Rent(entries.Length);
        Hash256 hash = NativePatriciaBulkSet.BulkSet(new NativeLogOnlySource(), pending, log, null, address, root.Hash, entries);
        return new FixtureRoot(hash.ValueHash256, log.ResolveRoot(address, hash.ValueHash256));
    }

    internal static IEnumerable<(TreePath Path, ulong Pointer, NativeTrieNodeLog.FixtureRecord Record)> EnumerateNodes(this NativeTrieNodeLog log, ulong root) =>
        log.EnumerateFixtureNodes(root);

    private sealed class NativeLogOnlySource : INativeNodeStore
    {
        public bool Fetch(in TreePath path, in ValueHash256 hash, NativeNodeReceiver receiver) => false;
    }

    private static readonly ConditionalWeakTable<SnapshotContent, FixtureNodes> _fixtureNodes = [];

    /// <summary>Connects supplied state nodes to the snapshot root, synthesizing missing ancestors.</summary>
    /// <remarks>Supplied parent RLP is authoritative; inputs it does not reference remain unreachable.</remarks>
    public static ulong SetStateNodeRlp(this SnapshotContent content, NativeTrieNodeLog log, in TreePath path, string rlpHex) =>
        content.SetStateNodeRlp(log, path, Bytes.FromHexString(rlpHex));

    /// <inheritdoc cref="SetStateNodeRlp(SnapshotContent, NativeTrieNodeLog, in TreePath, string)"/>
    public static ulong SetStateNodeRlp(this SnapshotContent content, NativeTrieNodeLog log, in TreePath path, byte[] rlp)
    {
        FixtureTrie trie = _fixtureNodes.GetValue(content, static _ => new FixtureNodes()).State;
        if (!content.HasStateRoot || content.StateRootPointer != trie.RootPointer) trie.Nodes.Clear();
        ulong pointer = trie.Set(log, null, path, rlp);
        content.StateRootPointer = trie.RootPointer;
        content.HasStateRoot = true;
        return pointer;
    }

    /// <summary>Connects the supplied storage node to its address's root, synthesizing missing ancestors.</summary>
    public static ulong SetStorageNodeRlp(this SnapshotContent content, NativeTrieNodeLog log, Hash256 address, in TreePath path, string rlpHex) =>
        content.SetStorageNodeRlp(log, address, path, Bytes.FromHexString(rlpHex));

    /// <inheritdoc cref="SetStorageNodeRlp(SnapshotContent, NativeTrieNodeLog, Hash256, in TreePath, string)"/>
    public static ulong SetStorageNodeRlp(this SnapshotContent content, NativeTrieNodeLog log, Hash256 address, in TreePath path, byte[] rlp)
    {
        FixtureNodes fixtures = _fixtureNodes.GetValue(content, static _ => new FixtureNodes());
        if (!fixtures.Storage.TryGetValue(address, out FixtureTrie? trie)) fixtures.Storage[address] = trie = new();
        if (!content.StorageRoots.TryGetValue(address, out ulong root) || root != trie.RootPointer) trie.Nodes.Clear();
        ulong pointer = trie.Set(log, address, path, rlp);
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

        public ulong Set(NativeTrieNodeLog log, Hash256? address, in TreePath path, byte[] rlp)
        {
            TreePath normalizedPath = CanonicalPath(path.Path, path.Length);
            Nodes[normalizedPath] = rlp;
            RootPointer = Build(log, address, TreePath.Empty, Nodes.ToArray()).Pointer;
            return log.TryGetNodePointer(RootPointer, path, out ulong pointer) ? pointer : AppendNode(log, address, path, rlp);
        }

        private static FixtureRoot Build(NativeTrieNodeLog log, Hash256? address, TreePath path, KeyValuePair<TreePath, byte[]>[] nodes)
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
                    references[0] = Build(log, address, childPath, nodes);
                    byte[] key = Enumerable.Range(path.Length, shared - path.Length).Select(i => (byte)nodes[0].Key[i]).ToArray();
                    rlp = ExtensionRlp(key, references[0].Hash);
                }
                else
                {
                    TrieNode branch = new(NodeType.Branch);
                    for (int i = 0; i < references.Length; i++)
                    {
                        TreePath childPath = path.Append(i);
                        KeyValuePair<TreePath, byte[]>[] descendants = nodes.Where(n => n.Key.Length >= childPath.Length && n.Key.StartsWith(childPath)).ToArray();
                        if (descendants.Length == 0) continue;
                        references[i] = Build(log, address, childPath, descendants);
                        branch.SetChild(i, new TrieNode(NodeType.Unknown, references[i].ToHash256()));
                    }
                    rlp = EncodeNode(branch);
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
                        if (descendants.Length > 0) child = Build(log, address, childPath, descendants);
                    }
                    if (child.Pointer != 0 && child.Hash == expectedHash.ValueHash256) pointers[i] = child.Pointer;
                }
            }

            ulong pointer = log.AppendRlp(address, path, rlp, pointers, indexRoot: path.Length == 0);
            return new FixtureRoot(ValueKeccak.Compute(rlp), pointer);
        }
    }

    /// <summary>The RLP of a leaf with an empty key holding <paramref name="value"/>, a distinct valid trie node per value.</summary>
    public static byte[] LeafRlp(params byte[] value) => EncodeNode(new TrieNode(NodeType.Leaf) { Key = [], Value = new CappedArray<byte>(value) });

    internal static byte[] ExtensionRlp(byte[] key, in ValueHash256 childHash)
    {
        TrieNode extension = new(NodeType.Extension) { Key = key };
        extension.SetChild(0, new TrieNode(NodeType.Unknown, new Hash256(childHash)));
        return EncodeNode(extension);
    }

    internal static byte[] BranchRlp(params (int Index, ValueHash256 Hash)[] children)
    {
        TrieNode branch = new(NodeType.Branch);
        foreach ((int index, ValueHash256 hash) in children) branch.SetChild(index, new TrieNode(NodeType.Unknown, new Hash256(hash)));
        return EncodeNode(branch);
    }

    private static byte[] EncodeNode(TrieNode node)
    {
        TreePath path = TreePath.Empty;
        return node.RlpEncode(NullTrieNodeResolver.Instance, ref path).AsSpan().ToArray();
    }

    private static ulong AppendNode(NativeTrieNodeLog log, Hash256? address, in TreePath path, byte[] rlp) =>
        log.AppendRlp(address, path, rlp, indexRoot: path.Length == 0);

    internal static ulong AppendRecordFromRlp(this NativeTrieNodeLog log, Hash256? address, in TreePath path, in ValueHash256 hash, ReadOnlySpan<byte> rlp)
    {
        if (ValueKeccak.Compute(rlp) != hash) throw new ArgumentException("Fixture node hash does not match its RLP.", nameof(hash));
        return log.AppendRlp(address, path, rlp, indexRoot: path.Length == 0);
    }

    internal static bool TryGetNodePointer(this NativeTrieNodeLog log, ulong root, in TreePath path, out ulong pointer)
    {
        foreach ((TreePath nodePath, ulong candidate, _) in log.EnumerateFixtureNodes(root))
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

    internal static IEnumerable<(TreePath Path, ulong Pointer, NativeTrieNodeLog.FixtureRecord Record)> EnumerateFixtureNodes(this NativeTrieNodeLog log, ulong root)
    {
        if (root == 0) yield break;
        Stack<(TreePath Path, ulong Pointer)> pending = new();
        pending.Push((TreePath.Empty, root));
        while (pending.TryPop(out (TreePath Path, ulong Pointer) next))
        {
            NativeTrieNodeLog.FixtureRecord record = log.GetRecord(next.Pointer);
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
    /// <param name="log">The log <paramref name="populate"/> records nodes in; the snapshot retains it.</param>
    public static ReadOnlySnapshotBundle MakeBundle(ResourcePool pool, NativeTrieNodeLog log, Action<SnapshotContent> populate) =>
        new(SnapshotList(MakeSnapshot(pool, log, populate)), Substitute.For<IPersistence.IPersistenceReader>(),
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
