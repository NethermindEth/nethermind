// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Flat.NodeBuffer;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.PersistedSnapshots;
using Nethermind.State.Flat.PersistedSnapshots.Storage;
using Nethermind.State.Flat.ScopeProvider;
using Nethermind.Trie;
using Nethermind.Trie.NodeBuffer;
using Nethermind.Trie.Pruning;
using NSubstitute;

namespace Nethermind.State.Flat.Test;

public static class FlatTestHelpers
{
    private const int ScratchGenerationBytes = 64 * 1024;
    private const int RecordAlignment = 8;

    private static readonly ReadOnlySpanAction<byte, List<byte[]>> CollectNode = static (node, nodes) => nodes.Add(node.ToArray());

    public static Snapshot MakeSnapshot(IResourcePool pool, Action<SnapshotContent>? populate = null)
    {
        SnapshotContent content = pool.GetSnapshotContent(ResourcePool.Usage.MainBlockProcessing);
        populate?.Invoke(content);
        return new Snapshot(StateId.PreGenesis, StateId.PreGenesis, content, pool, ResourcePool.Usage.MainBlockProcessing, null);
    }

    /// <summary>A buffer with 64 KiB generations, so tests can rotate it cheaply.</summary>
    public static INodeRecordBuffer NewTestBuffer() => new NodeRecordBuffer(64 * 1024);

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
            using IDisposable pin = buffer.PinAll();
            ulong pointer = 0;
            RootPointer = Build(buffer, address, TreePath.Empty, Nodes.ToArray(), normalizedPath, ref pointer).Pointer;
            return pointer;
        }

        private static (ulong Pointer, byte[] Rlp) Build(INodeRecordBuffer buffer, Hash256? address, TreePath path, KeyValuePair<TreePath, byte[]>[] nodes, in TreePath target, ref ulong targetPointer)
        {
            byte[]? rlp = nodes.FirstOrDefault(n => n.Key == path).Value;
            ulong[] childPointers = new ulong[16];
            if (rlp is not null)
            {
                TrieNode node = new(NodeType.Unknown, rlp);
                node.ResolveNode(NullTrieNodeResolver.Instance, path);
                if (node.IsExtension && node.GetChildHash(0) is { } extensionChild)
                    childPointers[0] = BuildBelow(buffer, address, path.Append(node.Key), extensionChild, nodes, target, ref targetPointer);
                else if (node.IsBranch)
                    for (int i = 0; i < 16; i++)
                        if (node.GetChildHash(i) is { } branchChild) childPointers[i] = BuildBelow(buffer, address, path.Append(i), branchChild, nodes, target, ref targetPointer);
            }
            else
            {
                int shared = path.Length;
                while (shared < nodes[0].Key.Length && nodes.All(n => n.Key.Length > shared && n.Key[shared] == nodes[0].Key[shared])) shared++;
                if (shared > path.Length)
                {
                    (childPointers[0], byte[] child) = Build(buffer, address, nodes[0].Key.Truncate(shared), nodes, target, ref targetPointer);
                    rlp = ExtensionRlp(Enumerable.Range(path.Length, shared - path.Length).Select(i => (byte)nodes[0].Key[i]).ToArray(), child);
                }
                else
                {
                    byte[]?[] children = new byte[16][];
                    for (int i = 0; i < 16; i++)
                    {
                        TreePath childPath = path.Append(i);
                        KeyValuePair<TreePath, byte[]>[] descendants = Descendants(nodes, childPath);
                        if (descendants.Length > 0) (childPointers[i], children[i]) = Build(buffer, address, childPath, descendants, target, ref targetPointer);
                    }
                    rlp = BranchRlp(children);
                }
            }

            ulong pointer = buffer.AppendRecordFromRlp(address, path, ValueKeccak.Compute(rlp), rlp, childPointers);
            if (path == target) targetPointer = pointer;
            return (pointer, rlp);
        }

        /// <returns>The pointer of the built child, or zero when there is none or <paramref name="referencedHash"/> is not its hash.</returns>
        private static ulong BuildBelow(INodeRecordBuffer buffer, Hash256? address, in TreePath childPath, Hash256 referencedHash, KeyValuePair<TreePath, byte[]>[] nodes, in TreePath target, ref ulong targetPointer)
        {
            KeyValuePair<TreePath, byte[]>[] descendants = Descendants(nodes, childPath);
            if (descendants.Length == 0) return 0;

            ulong pointer = Build(buffer, address, childPath, descendants, target, ref targetPointer).Pointer;
            return buffer.TryLocate(pointer, out NodeRecord record) && record.Hash == referencedHash.ValueHash256 ? pointer : 0;
        }

        private static KeyValuePair<TreePath, byte[]>[] Descendants(KeyValuePair<TreePath, byte[]>[] nodes, TreePath path) =>
            nodes.Where(n => n.Key.Length >= path.Length && n.Key.StartsWith(path)).ToArray();
    }

    /// <summary>Writes the node <paramref name="rlp"/> to <paramref name="warmed"/> and indexes it at <paramref name="path"/>, as a warm-up does.</summary>
    /// <returns>The transient pointer of the written record.</returns>
    public static ulong Warm(WarmedNodeBuffer warmed, Hash256? address, in TreePath path, byte[] rlp)
    {
        using PrivateNodeRecordBuffer encoder = new(ScratchGenerationBytes);
        encoder.TryLocate(encoder.AppendRecordFromRlp(address, path, ValueKeccak.Compute(rlp), rlp, []), out NodeRecord record);
        ulong pointer = CopyRecord(warmed.StartWriteBatch, record.Span);
        warmed.Index(address, path, pointer);
        return pointer;
    }

    /// <summary>The RLP of the node <paramref name="load"/> hands over, as a record or as RLP, or null when it misses.</summary>
    public static byte[]? LoadRlp(Func<ReadOnlySpanAction<byte, List<byte[]>>, List<byte[]>, bool> load)
    {
        List<byte[]> nodes = [];
        if (!load(CollectNode, nodes)) return null;

        byte[] node = nodes.Single();
        // Node RLP is a list, while a record starts with its kind.
        if (node[0] >= 0xc0) return node;

        using PrivateNodeRecordBuffer copy = new(ScratchGenerationBytes);
        copy.TryLocate(CopyRecord(copy.StartWriteBatch, node), out NodeRecord record);
        return record.Rlp.ToArray();
    }

    private static ulong CopyRecord(Func<int, INodeRecordWriteBatch> startWriteBatch, ReadOnlySpan<byte> record)
    {
        using INodeRecordWriteBatch batch = startWriteBatch((record.Length + RecordAlignment - 1) & -RecordAlignment);
        record.CopyTo(batch.Bytes);
        return batch.BasePointer;
    }

    /// <summary>The RLP of a leaf with an empty key holding <paramref name="value"/>, a distinct valid trie node per value.</summary>
    public static byte[] LeafRlp(params byte[] value) => Rlp.Encode(Rlp.Encode(HexPrefix.ToBytes([], isLeaf: true)), Rlp.Encode(value)).Bytes;

    /// <summary>The RLP of an extension over the <paramref name="key"/> nibbles referencing <paramref name="childRlp"/> by hash.</summary>
    public static byte[] ExtensionRlp(byte[] key, byte[] childRlp) =>
        Rlp.Encode(Rlp.Encode(HexPrefix.ToBytes(key, isLeaf: false)), Rlp.Encode(Keccak.Compute(childRlp))).Bytes;

    /// <summary>The RLP of a valueless branch referencing <paramref name="children"/> by nibble and hash, null for none.</summary>
    /// <remarks>Short children are referenced by hash too, so each stays a record of its own.</remarks>
    public static byte[] BranchRlp(byte[]?[] children) =>
        Rlp.Encode([.. Enumerable.Range(0, 16).Select(i => children.ElementAtOrDefault(i) is { } child ? Rlp.Encode(Keccak.Compute(child)) : Rlp.OfEmptyByteArray), Rlp.OfEmptyByteArray]).Bytes;

    private static ulong AppendNode(INodeRecordBuffer buffer, Hash256? address, in TreePath path, byte[] rlp) =>
        buffer.AppendRecordFromRlp(address, path, ValueKeccak.Compute(rlp), rlp, []);

    /// <summary>Bulk-sets <paramref name="entries"/> over <paramref name="root"/> into <paramref name="buffer"/>, as a scope's commit does.</summary>
    public static (ValueHash256 Hash, ulong Pointer) BulkSet(ResourcePool pool, INodeRecordBuffer buffer, Hash256? address, (ValueHash256 Hash, ulong Pointer) root, in ArrayPoolListRef<PatriciaTree.BulkSetEntry> entries, bool canParallelize)
    {
        using SnapshotBundle bundle = new(MakeBundle(pool), buffer, pool, ResourcePool.Usage.MainBlockProcessing);
        NodeRecordTrie trie = new(bundle, address, root);
        trie.BulkSet(entries, canParallelize);
        return trie.Root;
    }

    /// <summary>
    /// Writes <paramref name="entry"/> under the pre-<c>(To, tier)</c> catalog key: big-endian To block, To root,
    /// big-endian depth. The row value layout is unchanged by the re-key, so the current catalog produces it.
    /// </summary>
    public static void PutLegacyCatalogRow(IDb catalogDb, CatalogEntry entry)
    {
        byte[] key = new byte[48];
        BinaryPrimitives.WriteInt64BigEndian(key, (long)entry.To.BlockNumber);
        entry.To.StateRoot.Bytes.CopyTo(key.AsSpan(8));
        BinaryPrimitives.WriteInt64BigEndian(key.AsSpan(40), (long)(entry.To.BlockNumber - entry.From.BlockNumber));

        using MemDb scratch = new();
        new SnapshotCatalog(scratch).Add(entry);
        catalogDb[key] = scratch.GetAllValues().Single();
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
            recordDetailedMetrics: false, PersistedSnapshotStack.Empty(), bufferPin: null);

    /// <inheritdoc cref="MakeBundle(ResourcePool, Action{SnapshotContent})"/>
    /// <param name="buffer">The buffer <paramref name="populate"/> records nodes in; the snapshot retains it.</param>
    public static ReadOnlySnapshotBundle MakeBundle(ResourcePool pool, INodeRecordBuffer buffer, Action<SnapshotContent> populate) =>
        new(SnapshotList(MakeSnapshot(pool, buffer, populate)), Substitute.For<IPersistence.IPersistenceReader>(),
            recordDetailedMetrics: false, PersistedSnapshotStack.Empty(), bufferPin: null);
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

internal sealed class ForwardingNodeRecordBuffer(INodeRecordBuffer inner) : INodeRecordBuffer
{
    public int CompletedWrites;
    public int CompletedRecords;
    public int OutstandingPins;

    public bool TryLocate(ulong pointer, out NodeRecord record) => inner.TryLocate(pointer, out record);
    public INodeRecordWriteBatch StartWriteBatch(int alignedTotalLength) => new ForwardedWriteBatch(this, inner.StartWriteBatch(alignedTotalLength));

    public ulong FindRoot(Hash256? address, in ValueHash256 root, ulong boundary) => inner.FindRoot(address, root, boundary);
    public void SetRoot(Hash256? address, in ValueHash256 root, ulong rootPointer) => inner.SetRoot(address, root, rootPointer);
    public INodeRecordBufferPin PinHead() => Wrap(inner.PinHead());
    public IDisposable PinAll() => inner.PinAll();
    public void Dispose() => inner.Dispose();

    private INodeRecordBufferPin Wrap(INodeRecordBufferPin pin)
    {
        Interlocked.Increment(ref OutstandingPins);
        return new ForwardedPin(this, pin);
    }

    private sealed class ForwardedWriteBatch(ForwardingNodeRecordBuffer owner, INodeRecordWriteBatch innerBatch) : INodeRecordWriteBatch
    {
        public ulong BasePointer => innerBatch.BasePointer;
        public Span<byte> Bytes => innerBatch.Bytes;

        public void Dispose()
        {
            int length = innerBatch.Bytes.Length;
            ulong basePointer = innerBatch.BasePointer;
            innerBatch.Dispose();
            int records = 0;
            // Records are 8-byte aligned and the unused tail is zero-filled, which reads as a record with no RLP.
            for (int offset = 0; offset < length && owner.TryLocate(basePointer + (ulong)offset, out NodeRecord record) && !record.Rlp.IsEmpty; offset += (record.Span.Length + 7) & -8) records++;
            Interlocked.Increment(ref owner.CompletedWrites);
            Interlocked.Add(ref owner.CompletedRecords, records);
        }
    }

    private sealed class ForwardedPin(ForwardingNodeRecordBuffer owner, INodeRecordBufferPin innerPin) : INodeRecordBufferPin
    {
        private int _disposed;
        public INodeRecordBuffer Buffer => owner;
        public ulong WriteBoundary => innerPin.WriteBoundary;
        public INodeRecordBufferPin Copy() => owner.Wrap(innerPin.Copy());

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            innerPin.Dispose();
            Interlocked.Decrement(ref owner.OutstandingPins);
        }
    }
}
