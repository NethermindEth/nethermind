// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test;
using Nethermind.Logging;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using NUnit.Framework;

namespace Nethermind.Store.Test;

public class PatriciaTrieWitnessGeneratorTests
{
    /// <summary>
    /// The generator must report exactly the pre-state nodes a real execution touches, using a read-capturing store
    /// that replays the reads and deletions as the ground truth.
    /// </summary>
    [TestCaseSource(nameof(WitnessCases))]
    public void Witness_matches_capture_during_mutation(Scenario scenario)
    {
        (TestMemDb db, Hash256 root) = BuildTrie(scenario.Existing);

        HashSet<Hash256AsKey> oracle = CaptureDuringMutation(db, root, scenario, out _);

        Assert.That(RunGenerator(db, root, scenario, parallelize: false), Is.EquivalentTo(oracle));
        Assert.That(RunGenerator(db, root, scenario, parallelize: true), Is.EquivalentTo(oracle));
    }

    /// <summary>
    /// The witness must be self-sufficient: a verifier holding only the generated nodes can serve every read and
    /// re-apply every write to recompute the post-state root, without ever hitting a missing node.
    /// </summary>
    [TestCaseSource(nameof(WitnessCases))]
    public void Witness_alone_serves_reads_and_writes(Scenario scenario)
    {
        (TestMemDb db, Hash256 root) = BuildTrie(scenario.Existing);

        Hash256 expectedRoot = ApplyWrites(new RawScopedTrieStore(db), root, scenario, out Dictionary<Hash256AsKey, byte[]> readValues);

        // Rebuild a store holding ONLY the witness nodes; Hash key scheme addresses nodes purely by keccak.
        Dictionary<Hash256AsKey, byte[]> witness = CollectWitness(db, root, scenario);
        NodeStorage witnessStorage = new(new TestMemDb(), INodeStorage.KeyScheme.Hash);
        foreach ((Hash256AsKey hash, byte[] rlp) in witness)
        {
            witnessStorage.Set(null, TreePath.Empty, new ValueHash256(hash.Value.Bytes), rlp);
        }

        IScopedTrieStore witnessStore = new RawScopedTrieStore(witnessStorage);
        PatriciaTree tree = new(witnessStore, LimboLogs.Instance) { RootHash = root };

        foreach (Hash256 key in scenario.Reads)
        {
            byte[] value = tree.Get(key.Bytes).ToArray();
            Assert.That(value, Is.EqualTo(readValues[key]), $"read mismatch for {key}");
        }

        foreach (Hash256 key in scenario.Deletes) tree.Set(key.Bytes, (byte[])null);
        foreach ((Hash256 key, byte[] value) in scenario.Writes) tree.Set(key.Bytes, value);
        tree.UpdateRootHash();

        Assert.That(tree.RootHash, Is.EqualTo(expectedRoot));
    }

    private static HashSet<Hash256AsKey> RunGenerator(TestMemDb db, Hash256 root, Scenario scenario, bool parallelize)
    {
        CollectingSink sink = new();
        IScopedTrieStore store = new RawScopedTrieStore(db);
        PatriciaTrieWitnessGenerator.Generate(store, root, BuildEntries(scenario), sink, parallelize);
        return [.. sink.Nodes.Keys];
    }

    private static Dictionary<Hash256AsKey, byte[]> CollectWitness(TestMemDb db, Hash256 root, Scenario scenario)
    {
        CollectingSink sink = new();
        IScopedTrieStore store = new RawScopedTrieStore(db);
        PatriciaTrieWitnessGenerator.Generate(store, root, BuildEntries(scenario), sink);
        return sink.Nodes;
    }

    private static PatriciaTrieWitnessGenerator.PathEntry[] BuildEntries(Scenario scenario)
    {
        // Reads and non-deleting writes both map to AccessType.Read; only Delete can collapse a branch.
        List<PatriciaTrieWitnessGenerator.PathEntry> entries = [];
        foreach (Hash256 key in scenario.Reads) entries.Add(new(key, PatriciaTrieWitnessGenerator.AccessType.Read));
        foreach ((Hash256 key, byte[] _) in scenario.Writes) entries.Add(new(key, PatriciaTrieWitnessGenerator.AccessType.Read));
        foreach (Hash256 key in scenario.Deletes) entries.Add(new(key, PatriciaTrieWitnessGenerator.AccessType.Delete));
        return [.. entries];
    }

    private static HashSet<Hash256AsKey> CaptureDuringMutation(TestMemDb db, Hash256 root, Scenario scenario, out Hash256 postRoot)
    {
        CapturingScopedTrieStore store = new(new RawScopedTrieStore(db));
        postRoot = ApplyWrites(store, root, scenario, out _);
        return [.. store.Captured.Keys];
    }

    /// <summary>Reads first (on the pristine trie), then applies the mutations, returning the post-state root.</summary>
    /// <remarks>Deletes are applied before writes on purpose: deletes-first is the collapse-maximizing order the generator must cover.</remarks>
    private static Hash256 ApplyWrites(IScopedTrieStore store, Hash256 root, Scenario scenario, out Dictionary<Hash256AsKey, byte[]> readValues)
    {
        PatriciaTree tree = new(store, LimboLogs.Instance) { RootHash = root };

        readValues = [];
        foreach (Hash256 key in scenario.Reads) readValues[key] = tree.Get(key.Bytes).ToArray();

        foreach (Hash256 key in scenario.Deletes) tree.Set(key.Bytes, (byte[])null);
        foreach ((Hash256 key, byte[] value) in scenario.Writes) tree.Set(key.Bytes, value);
        tree.UpdateRootHash();
        return tree.RootHash;
    }

    private static (TestMemDb db, Hash256 root) BuildTrie(List<(Hash256 key, byte[] value)> items)
    {
        TestMemDb db = new();
        IScopedTrieStore store = new RawScopedTrieStore(db);
        PatriciaTree tree = new(store, LimboLogs.Instance) { RootHash = Keccak.EmptyTreeHash };
        foreach ((Hash256 key, byte[] value) in items) tree.Set(key.Bytes, value);
        tree.Commit();
        return (db, tree.RootHash);
    }

    public sealed class Scenario(
        string name,
        List<(Hash256 key, byte[] value)> existing,
        List<Hash256> reads,
        List<Hash256> deletes,
        List<(Hash256 key, byte[] value)> writes)
    {
        public string Name { get; } = name;
        public List<(Hash256 key, byte[] value)> Existing { get; } = existing;
        public List<Hash256> Reads { get; } = reads;
        public List<Hash256> Deletes { get; } = deletes;
        public List<(Hash256 key, byte[] value)> Writes { get; } = writes;
        public override string ToString() => Name;
    }

    public static IEnumerable<TestCaseData> WitnessCases()
    {
        for (int seed = 0; seed < 20; seed++)
        {
            yield return Case(MakeFuzz(seed, 1 + new Random(seed).Next(800)));
        }

        // Large enough that a child branch also exceeds the parallelization threshold, exercising the nested parallel path and its flipCount + GetSpanOffset span recovery.
        yield return Case(MakeFuzz(seed: 101, size: 6000));
        yield return Case(MakeFuzz(seed: 102, size: 12000));

        // Reuse BulkSet's fixtures for their structural edge cases; a non-empty update value is a write, a null/empty one a deletion.
        int idx = 0;
        foreach (TestCaseData tc in PatriciaTreeBulkSetterTests.BulkSetTestGen())
        {
            List<(Hash256 key, byte[] value)> existing = (List<(Hash256 key, byte[] value)>)tc.Arguments[0]!;
            List<(Hash256 key, byte[] value)> updates = (List<(Hash256 key, byte[] value)>)tc.Arguments[1]!;
            List<(Hash256 key, byte[] value)> writes = updates.Where(u => u.value is { Length: > 0 }).ToList();
            List<Hash256> deletes = updates.Where(u => u.value is null or { Length: 0 }).Select(u => u.key).ToList();
            yield return Case(new Scenario($"bulkset {idx++}: {tc.TestName}", existing, [], deletes, writes));
        }

        // Collapse: deleting one of a two-child branch's children forces the lone sibling into the witness.
        List<(Hash256, byte[])> twoChild =
        [
            (Hash("aaaa000000000000000000000000000000000000000000000000000000000000"), Bytes.FromHexString("01")),
            (Hash("aaaabbbb00000000000000000000000000000000000000000000000000000000"), Bytes.FromHexString("02")),
        ];
        yield return Case(new Scenario("collapse one of two", twoChild, [], [Hash("aaaa000000000000000000000000000000000000000000000000000000000000")], []));

        // Chained collapse through an extension.
        List<(Hash256, byte[])> chained =
        [
            (Hash("1111111111111111111111111111111111111111111111111111111111111111"), Bytes.FromHexString("01")),
            (Hash("2222222222222222222222222222222222222222222222222222222222222222"), Bytes.FromHexString("02")),
            (Hash("2233333333333333333333333333333333333333333333333333333333333333"), Bytes.FromHexString("03")),
        ];
        yield return Case(new Scenario("chained collapse", chained, [],
        [
            Hash("2222222222222222222222222222222222222222222222222222222222222222"),
            Hash("2233333333333333333333333333333333333333333333333333333333333333"),
        ], []));

        // Absent-key read and absent-key delete over a populated trie.
        List<(Hash256, byte[])> populated = PatriciaTreeBulkSetterTests.GenRandomOfLength(50, 99);
        yield return Case(new Scenario("absent read/delete", populated,
            [Hash("dd")], [Hash("ee")], []));

        // Order-independence: an off-key insert must NOT keep the sibling out of the witness, since a delete-first order transiently collapses the branch.
        List<(Hash256, byte[])> splitBase =
        [
            (Hash("1a"), Bytes.FromHexString("01")),
            (Hash("2b"), Bytes.FromHexString("02")),
        ];
        yield return Case(new Scenario("off-key insert still needs sibling", splitBase,
            [], [Hash("1a")], [(Hash("1c"), Bytes.FromHexString("03"))]));

        List<(Hash256, byte[])> mixedBase = PatriciaTreeBulkSetterTests.GenRandomOfLength(40, 7);
        List<Hash256> mixedPresent = PresentKeys(mixedBase);
        yield return Case(new Scenario("mixed read/write/delete", mixedBase,
            mixedPresent.Take(5).ToList(),
            mixedPresent.Skip(5).Take(5).ToList(),
            [(Hash("abcdef0000000000000000000000000000000000000000000000000000000000"), Bytes.FromHexString("ff")),
             (mixedPresent[10], Bytes.FromHexString("aa"))]));

        static TestCaseData Case(Scenario s) => new TestCaseData(s).SetName(s.Name);
    }

    private static Scenario MakeFuzz(int seed, int size)
    {
        Random rng = new(seed);
        List<(Hash256 key, byte[] value)> existing = PatriciaTreeBulkSetterTests.GenRandomOfLength(size, seed);
        List<Hash256> present = PresentKeys(existing);

        List<Hash256> reads = PickSubset(present, rng);
        List<Hash256> deletes = PickSubset(present, rng);

        // Sprinkle in absent keys (a disjoint random set).
        List<(Hash256 key, byte[] value)> absent = PatriciaTreeBulkSetterTests.GenRandomOfLength(8, ~seed);
        for (int i = 0; i < rng.Next(5); i++) reads.Add(absent[i].key);
        for (int i = 0; i < rng.Next(5); i++) deletes.Add(absent[4 + i].key);

        return new Scenario($"fuzz {seed} (n={size})", existing, reads, deletes, []);
    }

    private static List<Hash256> PickSubset(List<Hash256> from, Random rng)
    {
        List<Hash256> picked = [];
        foreach (Hash256 key in from)
        {
            if (rng.NextDouble() < 0.3) picked.Add(key);
        }
        return picked;
    }

    private static List<Hash256> PresentKeys(List<(Hash256 key, byte[] value)> items) =>
        items.Where(it => it.value is { Length: > 0 }).Select(it => it.key).Distinct().ToList();

    private static Hash256 Hash(string hex) => new(hex.Length >= 64 ? hex[..64] : hex.PadRight(64, '0'));

    private sealed class CollectingSink : PatriciaTrieWitnessGenerator.ISink
    {
        private readonly object _lock = new();
        public Dictionary<Hash256AsKey, byte[]> Nodes { get; } = [];

        // Locked for the parallel walk; TryAdd asserts the "each node reported exactly once" contract.
        public void Add(in TreePath path, TrieNode node)
        {
            byte[] rlp = node.FullRlp.ToArray();
            lock (_lock)
            {
                Assert.That(Nodes.TryAdd(node.Keccak!, rlp), Is.True, $"node reported more than once: {node.Keccak}");
            }
        }
    }

    /// <summary>Wraps a scoped store and records the RLP of every node whose data is loaded from the backing db.</summary>
    private sealed class CapturingScopedTrieStore(IScopedTrieStore baseStore) : IScopedTrieStore
    {
        public Dictionary<Hash256AsKey, byte[]> Captured { get; } = [];

        public TrieNode FindCachedOrUnknown(in TreePath path, Hash256 hash) => baseStore.FindCachedOrUnknown(in path, hash);

        public byte[] LoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None) => Capture(hash, baseStore.LoadRlp(in path, hash, flags));

        public byte[] TryLoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None) => Capture(hash, baseStore.TryLoadRlp(in path, hash, flags));

        private byte[] Capture(Hash256 hash, byte[] rlp)
        {
            if (rlp is not null) Captured[hash] = rlp;
            return rlp;
        }

        public ITrieNodeResolver GetStorageTrieNodeResolver(Hash256 address) => baseStore.GetStorageTrieNodeResolver(address);

        public INodeStorage.KeyScheme Scheme => baseStore.Scheme;

        public ICommitter BeginCommit(TrieNode root, WriteFlags writeFlags = WriteFlags.None) => baseStore.BeginCommit(root, writeFlags);
    }
}
