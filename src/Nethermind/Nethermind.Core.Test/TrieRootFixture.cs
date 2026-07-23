// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.State;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.Core.Test;

/// <summary>
/// A deterministic prepared parent trie plus one final update batch, with the Patricia-calculated
/// expected root and persisted nodes, for comparing trie root-calculation implementations over
/// identical inputs.
/// </summary>
/// <remarks>
/// Keys and values are derived from keccak chains over (seed, domain, index), so a given
/// (kind, seed, sizes) tuple always produces byte-identical inputs and expected outputs. The parent
/// trie is committed into <see cref="ParentStorage"/> and never mutated afterwards, so one fixture
/// instance can back many measurement iterations. The expected outcome is produced by applying
/// <see cref="Updates"/> with the existing Patricia implementation and recording every node it
/// commits. All content is in-memory; instances are intended to live for the duration of a
/// benchmark or test process.
/// </remarks>
public sealed class TrieRootFixture
{
    /// <summary>Selects the leaf-value encoding: account RLP for state tries, slot RLP for storage tries.</summary>
    public enum TrieKind
    {
        State,
        Storage,
    }

    /// <summary>A node the reference Patricia implementation persisted while applying the updates.</summary>
    public readonly record struct PersistedTrieNode(TreePath Path, Hash256 Hash, byte[] Rlp);

    private const byte ParentKeyDomain = 0;
    private const byte InsertKeyDomain = 1;

    private TrieRootFixture(
        string name,
        TrieKind kind,
        Hash256 parentRoot,
        NodeStorage parentStorage,
        PatriciaTree.BulkSetEntry[] updates,
        Hash256 expectedRoot,
        IReadOnlyList<PersistedTrieNode> expectedNodes)
    {
        Name = name;
        Kind = kind;
        ParentRoot = parentRoot;
        ParentStorage = parentStorage;
        Updates = updates;
        ExpectedRoot = expectedRoot;
        ExpectedNodes = expectedNodes;
    }

    /// <summary>Display name; the frozen gate fixtures are addressed by name.</summary>
    public string Name { get; }

    /// <summary>The leaf-value encoding this fixture was generated with.</summary>
    public TrieKind Kind { get; }

    /// <summary>Root of the committed parent trie, before the updates.</summary>
    public Hash256 ParentRoot { get; }

    /// <summary>Committed parent nodes; read-only after construction.</summary>
    public NodeStorage ParentStorage { get; }

    /// <summary>
    /// The normalized final update batch: unique keys with final encoded leaf values, where an
    /// empty value means deletion. Kept in generation order; implementations sort their own copy.
    /// </summary>
    public PatriciaTree.BulkSetEntry[] Updates { get; }

    /// <summary>Root after applying <see cref="Updates"/>, as calculated by Patricia.</summary>
    public Hash256 ExpectedRoot { get; }

    /// <summary>Every (path, hash, RLP) node Patricia persisted for the update batch, including the root.</summary>
    public IReadOnlyList<PersistedTrieNode> ExpectedNodes { get; }

    /// <summary>
    /// Creates one of the named, frozen fixtures used by the root-calculation performance gate.
    /// </summary>
    /// <remarks>
    /// Sizes approximate the reproducible payload workloads: realblocks blocks touch a few
    /// thousand accounts and a few hundred slots per hot storage trie, while superblocks
    /// concentrate tens of thousands of updates in a single dominant trie. Parent tries are
    /// capped at one million keys to keep construction tractable; both compared implementations
    /// read the same parents, so the comparison is unaffected by the smaller depth.
    /// </remarks>
    public static TrieRootFixture CreateGateFixture(string name) => name switch
    {
        "storage-tiny" => Create(name, TrieKind.Storage, seed: 1, parentCount: 5_000, modifyCount: 6, insertCount: 1, deleteCount: 1),
        "storage-realblocks" => Create(name, TrieKind.Storage, seed: 2, parentCount: 100_000, modifyCount: 150, insertCount: 30, deleteCount: 20),
        "state-realblocks" => Create(name, TrieKind.State, seed: 3, parentCount: 1_000_000, modifyCount: 1_875, insertCount: 375, deleteCount: 250),
        "storage-dominant" => Create(name, TrieKind.Storage, seed: 4, parentCount: 1_000_000, modifyCount: 18_750, insertCount: 3_750, deleteCount: 2_500),
        "state-superblock" => Create(name, TrieKind.State, seed: 5, parentCount: 1_000_000, modifyCount: 37_500, insertCount: 7_500, deleteCount: 5_000),
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Unknown gate fixture"),
    };

    /// <summary>
    /// Builds a fixture: a committed parent trie of <paramref name="parentCount"/> keys, an update
    /// batch of <paramref name="modifyCount"/> value changes to existing keys,
    /// <paramref name="insertCount"/> new keys, and <paramref name="deleteCount"/> deletions of
    /// existing keys, plus the Patricia-calculated expected outcome. Deterministic in
    /// (<paramref name="kind"/>, <paramref name="seed"/>, sizes).
    /// </summary>
    public static TrieRootFixture Create(
        string name,
        TrieKind kind,
        int seed,
        int parentCount,
        int modifyCount,
        int insertCount,
        int deleteCount)
    {
        // Modified keys come from the lower half of the parent index space and deleted keys from
        // the upper half, so the three update groups are disjoint by construction.
        ArgumentOutOfRangeException.ThrowIfGreaterThan(modifyCount, parentCount / 2);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(deleteCount, parentCount - parentCount / 2);

        NodeStorage parentStorage = new(new MemDb());
        Hash256 parentRoot = BuildParent(parentStorage, kind, seed, parentCount);

        PatriciaTree.BulkSetEntry[] updates = BuildUpdates(kind, seed, parentCount, modifyCount, insertCount, deleteCount);

        RecordingTrieStore recorder = new(new RawScopedTrieStore(parentStorage));
        PatriciaTree expectedTree = new(recorder, parentRoot, true, NullLogManager.Instance);
        using (ArrayPoolListRef<PatriciaTree.BulkSetEntry> scratch = new(updates))
        {
            expectedTree.BulkSet(in scratch, PatriciaTree.Flags.DoNotParallelize);
        }

        expectedTree.Commit();

        return new TrieRootFixture(name, kind, parentRoot, parentStorage, updates, expectedTree.RootHash, recorder.Committed);
    }

    private static Hash256 BuildParent(NodeStorage parentStorage, TrieKind kind, int seed, int parentCount)
    {
        PatriciaTree tree = new(new RawScopedTrieStore(parentStorage), Keccak.EmptyTreeHash, true, NullLogManager.Instance);
        using (ArrayPoolListRef<PatriciaTree.BulkSetEntry> entries = new(parentCount))
        {
            for (int i = 0; i < parentCount; i++)
            {
                ValueHash256 key = DeriveKey(seed, ParentKeyDomain, i);
                entries.Add(CreateEntry(kind, in key, generation: 0));
            }

            tree.BulkSet(in entries);
        }

        tree.Commit();
        return tree.RootHash;
    }

    private static PatriciaTree.BulkSetEntry[] BuildUpdates(
        TrieKind kind,
        int seed,
        int parentCount,
        int modifyCount,
        int insertCount,
        int deleteCount)
    {
        PatriciaTree.BulkSetEntry[] updates = new PatriciaTree.BulkSetEntry[modifyCount + insertCount + deleteCount];
        int next = 0;

        int modifyStride = modifyCount == 0 ? 1 : Math.Max(1, parentCount / 2 / modifyCount);
        for (int i = 0; i < modifyCount; i++)
        {
            ValueHash256 key = DeriveKey(seed, ParentKeyDomain, i * modifyStride);
            updates[next++] = CreateEntry(kind, in key, generation: 1);
        }

        for (int i = 0; i < insertCount; i++)
        {
            ValueHash256 key = DeriveKey(seed, InsertKeyDomain, i);
            updates[next++] = CreateEntry(kind, in key, generation: 1);
        }

        int deleteStride = deleteCount == 0 ? 1 : Math.Max(1, (parentCount - parentCount / 2) / deleteCount);
        for (int i = 0; i < deleteCount; i++)
        {
            ValueHash256 key = DeriveKey(seed, ParentKeyDomain, parentCount / 2 + i * deleteStride);
            updates[next++] = new PatriciaTree.BulkSetEntry(in key, []);
        }

        return updates;
    }

    private static ValueHash256 DeriveKey(int seed, byte domain, int index)
    {
        Span<byte> material = stackalloc byte[9];
        BinaryPrimitives.WriteInt32LittleEndian(material, seed);
        material[4] = domain;
        BinaryPrimitives.WriteInt32LittleEndian(material[5..], index);
        return ValueKeccak.Compute(material);
    }

    private static PatriciaTree.BulkSetEntry CreateEntry(TrieKind kind, in ValueHash256 key, int generation)
    {
        Span<byte> material = stackalloc byte[36];
        key.Bytes.CopyTo(material);
        BinaryPrimitives.WriteInt32LittleEndian(material[32..], generation);
        ValueHash256 derived = ValueKeccak.Compute(material);

        return kind == TrieKind.State
            ? new PatriciaTree.BulkSetEntry(in key, EncodeAccount(in derived))
            : StorageTree.CreateBulkSetEntry(in key, DeriveStorageValue(in derived));
    }

    private static byte[] EncodeAccount(in ValueHash256 derived)
    {
        ReadOnlySpan<byte> bytes = derived.Bytes;
        ulong nonce = BinaryPrimitives.ReadUInt16LittleEndian(bytes);
        UInt256 balance = new(BinaryPrimitives.ReadUInt64LittleEndian(bytes[2..]));

        // Roughly one in four accounts is a contract carrying a storage root and code hash,
        // giving the two account-RLP lengths seen in a real state trie.
        Account account = bytes[10] % 4 == 0
            ? new Account(nonce, balance,
                ValueKeccak.Compute(bytes[..16]).ToCommitment(),
                ValueKeccak.Compute(bytes[16..]).ToCommitment())
            : new Account(nonce, balance);

        return AccountDecoder.Instance.EncodeAsBytes(account);
    }

    private static byte[] DeriveStorageValue(in ValueHash256 derived)
    {
        ReadOnlySpan<byte> bytes = derived.Bytes;

        // Length skew approximating mainnet slots: mostly small integers, some mid-size values,
        // some full words (addresses, hashes).
        int selector = bytes[0] % 100;
        int length = selector < 60 ? 1 + bytes[1] % 8
            : selector < 85 ? 9 + bytes[1] % 12
            : 21 + bytes[1] % 12;

        byte[] value = bytes.Slice(32 - length, length).ToArray();
        if (value[0] == 0)
        {
            value[0] = 1; // keep the canonical no-leading-zero form
        }

        return value;
    }

    /// <summary>
    /// An <see cref="IScopedTrieStore"/> that reads through to an inner store but captures
    /// committed nodes instead of persisting them, leaving the inner store unchanged.
    /// </summary>
    public sealed class RecordingTrieStore(IScopedTrieStore inner, bool collectNodes = true) : IScopedTrieStore
    {
        private readonly bool _collectNodes = collectNodes;

        /// <summary>Nodes committed through this store, in commit order; empty when constructed with <c>collectNodes: false</c>.</summary>
        public List<PersistedTrieNode> Committed { get; } = [];

        /// <summary>Number of nodes committed through this store, counted even when collection is off.</summary>
        public int CommittedCount { get; private set; }

        /// <inheritdoc/>
        public TrieNode FindCachedOrUnknown(in TreePath path, Hash256 hash) => inner.FindCachedOrUnknown(in path, hash);

        /// <inheritdoc/>
        public byte[]? LoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None) => inner.LoadRlp(in path, hash, flags);

        /// <inheritdoc/>
        public byte[]? TryLoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None) => inner.TryLoadRlp(in path, hash, flags);

        /// <inheritdoc/>
        public ITrieNodeResolver GetStorageTrieNodeResolver(Hash256? address) =>
            address is null ? this : inner.GetStorageTrieNodeResolver(address);

        /// <inheritdoc/>
        public INodeStorage.KeyScheme Scheme => inner.Scheme;

        /// <inheritdoc/>
        public ICommitter BeginCommit(TrieNode? root, WriteFlags writeFlags = WriteFlags.None) => new RecordingCommitter(this);

        private sealed class RecordingCommitter(RecordingTrieStore store) : ICommitter
        {
            public TrieNode CommitNode(ref TreePath path, TrieNode node)
            {
                if (node.Keccak is null)
                {
                    throw new TrieException($"Committed node without a resolved hash at {path}: {node}");
                }

                store.CommittedCount++;
                if (store._collectNodes)
                {
                    store.Committed.Add(new PersistedTrieNode(path, node.Keccak, node.FullRlp.AsSpan().ToArray()));
                }

                return node;
            }

            public void Dispose() { }
        }
    }
}
