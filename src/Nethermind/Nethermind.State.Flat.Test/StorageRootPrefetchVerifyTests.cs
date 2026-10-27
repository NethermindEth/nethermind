// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.Persistence.TrieNodeLog;
using Nethermind.State.Flat.PersistedSnapshots;
using Nethermind.State.Flat.ScopeProvider;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

/// <summary>
/// Verifier (xp2v) tests of the storage-root prefetch: a persistence that answers some of the prefetch's reads with another
/// node's bytes, and blocks on one node cache shared across blocks while trie readers and the warmer walk the same nodes.
/// They check the roots, and that every node found in a parent's slot is the node that slot refers to.
/// </summary>
public class StorageRootPrefetchVerifyTests
{
    private const int TrieSlots = 100_000;
    private const int Changes = 1_000;
    private const int Blocks = 16;
    private const int ReaderThreads = 4;

    internal static readonly Address Contract = TestItem.AddressA;
    private static readonly Hash256 ContractHash = Contract.ToAccountPath.ToHash256();
    private static readonly PropertyInfo IsWarmerOwned =
        typeof(TrieNode).GetProperty("IsWarmerOwned", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("TrieNode.IsWarmerOwned not found");

    public enum Kind { Delete, Update, Mixed }

    /// <summary>
    /// The prefetch's reader threads get, on their first read of a leaf, the bytes of a neighbouring leaf: what a path-keyed
    /// store holding another version of the node answers. The prefetch must refuse them, so between the prefetch and the
    /// bulk write every slot holds the node its parent refers to, and the root is right.
    /// </summary>
    [Test]
    public void Prefetch_links_only_nodes_that_match_their_reference([Values] Kind kind)
    {
        using Chain chain = new(TrieSlots);
        (UInt256 Slot, UInt256 Value)[] changes = chain.PickChanges(kind, new Random(3));
        ForeignLeafReader reader = new(chain.Persistence.CreateReader(), ContractHash);
        using WarmReadPool pool = new(16);
        FlatDbConfig config = new() { ApplyStorageWritesOnIdleThread = false, StorageRootPrefetchMinChanges = 1 };
        SnapshotBundle bundle = chain.CreateBundle(reader, new TrieNodeCache(config, LimboLogs.Instance), ResourcePool.Usage.MainBlockProcessing);
        FlatWorldStateScope scope = new(chain.State, bundle, new NullCodeDb(), new HandOverTarget(), config, new NoopTrieWarmer(),
            LimboLogs.Instance, warmReadPool: new Lazy<WarmReadPool>(() => pool));
        try
        {
            FlatStorageTree flat = (FlatStorageTree)scope.CreateStorageTree(Contract);
            StorageTree tree = new(new StorageTrieStoreAdapter(bundle, ContractHash), chain.StorageRoot, LimboLogs.Instance);
            InspectingPrefetcher inspecting = new(flat);
            Hash256? root = null;
            using (TrieStoreScopeProvider.StorageTreeBulkWriteBatch batch = new(changes.Length, tree, (_, r) => root = r, Contract, prefetcher: inspecting))
            {
                foreach ((UInt256 slot, UInt256 value) in changes.Where(c => !c.Value.IsZero)) batch.Set(slot, value);
                foreach ((UInt256 slot, UInt256 value) in changes.Where(c => c.Value.IsZero)) batch.Set(slot, value);
            }

            Hash256 expected = chain.Apply(changes);
            (int contracts, int loads, int rounds, int misses, _) = scope.StorageRootPrefetchCounts;
            TestContext.Out.WriteLine($"XP2V links kind={kind} contracts={contracts} loads={loads} rounds={rounds} foreign={reader.ForeignServed} " +
                                      $"misses={misses} linked={inspecting.Linked} errors={inspecting.Errors.Count}");
            foreach (string error in inspecting.Errors.Take(10)) TestContext.Out.WriteLine($"XP2V link error: {error}");

            using (Assert.EnterMultipleScope())
            {
                Assert.That(contracts, Is.EqualTo(1), "the prefetch ran");
                Assert.That(reader.ForeignServed, Is.GreaterThan(0), "the prefetch was offered foreign bytes");
                // Every foreign read is refused, and the refusal shows in the counters: failures count only exceptions.
                Assert.That(misses, Is.EqualTo(reader.ForeignServed), "a refused node is counted as a miss");
                Assert.That(inspecting.Linked, Is.GreaterThan(Changes), "the prefetch linked the nodes it resolved");
                Assert.That(inspecting.Errors, Is.Empty, "a slot holds a node other than the one its parent refers to");
                Assert.That(root, Is.EqualTo(expected));
            }
        }
        finally
        {
            scope.Dispose();
        }
    }

    /// <summary>
    /// Blocks in a row on one shared node cache, the cache filled from each block's nodes in the background as on a node,
    /// with a real warmer loading the block's paths and reader threads walking the pre-block trie through the same cached
    /// nodes while the prefetch links into them and the bulk write runs. Every block's root, every reader's value and
    /// every link of the shared pre-block trie are checked. The off case is the control.
    /// </summary>
    [Test]
    public void Blocks_on_a_shared_node_cache_keep_their_roots_while_readers_and_the_warmer_walk_the_same_nodes([Values] bool prefetch)
    {
        using Chain chain = new(TrieSlots);
        using WarmReadPool pool = new(16);
        FlatDbConfig config = new() { ApplyStorageWritesOnIdleThread = false, StorageRootPrefetchMinChanges = prefetch ? 256 : 0 };
        TrieNodeCache cache = new(config, LimboLogs.Instance);
        Random random = new(21);
        Task populate = Task.CompletedTask;
        ConcurrentQueue<string> errors = new();
        long reads = 0;
        int sharedRootsChecked = 0;
        int linksChecked = 0;
        int prefetched = 0;

        for (int block = 1; block <= Blocks; block++)
        {
            // Every other block starts before the previous block's nodes reach the cache, as the populator is asynchronous.
            if (block % 2 == 1) populate.Wait();

            (UInt256 Slot, UInt256 Value)[] changes = chain.PickChanges(Kind.Mixed, random);
            Dictionary<UInt256, UInt256> before = new(chain.Slots);
            Hash256 preRoot = chain.StorageRoot;
            UInt256[] readSlots = [.. changes.Select(c => c.Slot), .. chain.Slots.Keys.Take(500)];

            TrieWarmer warmer = new(LimboLogs.Instance, config);
            JitterReader reader = new(chain.Persistence.CreateReader(), block);
            SnapshotBundle bundle = chain.CreateBundle(reader, cache, ResourcePool.Usage.MainBlockProcessing);
            HandOverTarget target = new();
            FlatWorldStateScope scope = new(chain.State, bundle, new NullCodeDb(), target, config, warmer, LimboLogs.Instance,
                warmReadPool: new Lazy<WarmReadPool>(() => pool));
            using CancellationTokenSource stop = new();
            Task<long>[] readers = [.. Enumerable.Range(0, ReaderThreads).Select(r => Task.Factory.StartNew(
                () => ReadUntilStopped(chain, cache, preRoot, before, readSlots, 100 * block + r, stop.Token, errors), TaskCreationOptions.LongRunning))];
            Hash256 root;
            try
            {
                FlatStorageTree storageTree = (FlatStorageTree)scope.CreateStorageTree(Contract);
                // The block's commit-time hints: the warmer is still loading these paths when the batch starts.
                foreach ((UInt256 slot, UInt256 value) in changes) storageTree.HintSet(slot, value);

                using (IWorldStateScopeProvider.IWorldStateWriteBatch batch = scope.StartWriteBatch(1))
                {
                    Write(batch, Contract, changes);
                }

                prefetched += scope.StorageRootPrefetchCounts.Contracts;
                stop.Cancel();
                foreach (Task<long> r in readers) reads += r.GetAwaiter().GetResult();

                // The pre-block trie as the cache shares it: the nodes the prefetch, the warmer and the readers linked into.
                if (cache.TryGet(ContractHash, TreePath.Empty, preRoot, out TrieNode? shared))
                {
                    sharedRootsChecked++;
                    List<string> linkErrors = [];
                    linksChecked += CheckLinks(shared, linkErrors);
                    foreach (string e in linkErrors) errors.Enqueue($"block {block}: {e}");
                }

                scope.Commit((ulong)block);
                root = scope.Get(Contract)!.StorageRoot;
                chain.State = new StateId((ulong)block, scope.RootHash);
            }
            finally
            {
                stop.Cancel();
                Task.WaitAll(readers);
                scope.Dispose();
                warmer.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }

            TransientResource transient = target.Transient ?? throw new InvalidOperationException("no transient resource handed over");
            Task previous = populate;
            populate = Task.Run(() =>
            {
                previous.Wait();
                try
                {
                    cache.Add(transient);
                }
                finally
                {
                    transient.ReleaseLease();
                }
            });

            chain.Persist(target.Snapshot ?? throw new InvalidOperationException("no snapshot handed over"));
            Hash256 expected = chain.Apply(changes);
            if (root != expected) errors.Enqueue($"block {block}: root {root}, expected {expected}");
        }

        populate.Wait();
        TestContext.Out.WriteLine($"XP2V shared prefetch={prefetch} blocks={Blocks} prefetched={prefetched} reads={reads} " +
                                  $"shared_roots_checked={sharedRootsChecked} links_checked={linksChecked} errors={errors.Count}");
        foreach (string error in errors.Take(10)) TestContext.Out.WriteLine($"XP2V shared error: {error}");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(errors, Is.Empty);
            Assert.That(prefetched, Is.EqualTo(prefetch ? Blocks : 0));
            Assert.That(reads, Is.GreaterThan(0));
            Assert.That(sharedRootsChecked, Is.GreaterThan(Blocks / 4), "the pre-block root came from the shared cache");
        }
    }

    /// <summary>
    /// Two large contracts' prefetches at once on one reader pool, which admits one caller at a time: both finish, their
    /// links are right and so are their roots.
    /// </summary>
    [Test]
    public void Two_prefetches_at_once_share_one_reader_pool()
    {
        using Chain first = new(TrieSlots / 2);
        using Chain second = new(TrieSlots / 2);
        using WarmReadPool pool = new(16);
        using Barrier start = new(2);
        Task<string?>[] runs = [.. new[] { first, second }.Select((chain, i) =>
            Task.Factory.StartNew(() => PrefetchAndWrite(chain, pool, new Random(40 + i), start), TaskCreationOptions.LongRunning))];

        Assert.That(Task.WaitAll(runs, TimeSpan.FromSeconds(100)), Is.True, "a prefetch did not finish");
        Assert.That(runs.Select(r => r.Result), Is.All.Null);
    }

    private static string? PrefetchAndWrite(Chain chain, WarmReadPool pool, Random random, Barrier start)
    {
        (UInt256 Slot, UInt256 Value)[] changes = chain.PickChanges(Kind.Mixed, random);
        FlatDbConfig config = new() { ApplyStorageWritesOnIdleThread = false, StorageRootPrefetchMinChanges = 1 };
        SnapshotBundle bundle = chain.CreateBundle(chain.Persistence.CreateReader(), new TrieNodeCache(config, LimboLogs.Instance), ResourcePool.Usage.MainBlockProcessing);
        using FlatWorldStateScope scope = new(chain.State, bundle, new NullCodeDb(), new HandOverTarget(), config, new NoopTrieWarmer(),
            LimboLogs.Instance, warmReadPool: new Lazy<WarmReadPool>(() => pool));
        FlatStorageTree flat = (FlatStorageTree)scope.CreateStorageTree(Contract);
        StorageTree tree = new(new StorageTrieStoreAdapter(bundle, ContractHash), chain.StorageRoot, LimboLogs.Instance);
        InspectingPrefetcher inspecting = new(flat);
        Hash256? root = null;
        using (TrieStoreScopeProvider.StorageTreeBulkWriteBatch batch = new(changes.Length, tree, (_, r) => root = r, Contract, prefetcher: inspecting))
        {
            foreach ((UInt256 slot, UInt256 value) in changes.Where(c => !c.Value.IsZero)) batch.Set(slot, value);
            foreach ((UInt256 slot, UInt256 value) in changes.Where(c => c.Value.IsZero)) batch.Set(slot, value);
            // Both batches reach their prefetch together.
            start.SignalAndWait(TimeSpan.FromSeconds(30));
        }

        Hash256 expected = chain.Apply(changes);
        (int contracts, int loads, int rounds, int misses, _) = scope.StorageRootPrefetchCounts;
        TestContext.Out.WriteLine($"XP2V two-pools contracts={contracts} loads={loads} rounds={rounds} misses={misses} linked={inspecting.Linked} errors={inspecting.Errors.Count} " +
                                  $"prefetch_ticks={inspecting.Start}..{inspecting.End} freq={System.Diagnostics.Stopwatch.Frequency}");
        if (contracts != 1 || inspecting.Linked == 0) return $"prefetch did not run: contracts={contracts} linked={inspecting.Linked}";
        if (inspecting.Errors.Count != 0) return $"{inspecting.Errors.Count} bad links, first: {inspecting.Errors[0]}";
        return root == expected ? null : $"root {root}, expected {expected}";
    }

    private static long ReadUntilStopped(Chain chain, TrieNodeCache cache, Hash256 storageRoot, Dictionary<UInt256, UInt256> expected,
        UInt256[] slots, int seed, CancellationToken token, ConcurrentQueue<string> errors)
    {
        long reads = 0;
        SnapshotBundle bundle = chain.CreateBundle(chain.Persistence.CreateReader(), cache, ResourcePool.Usage.ReadOnlyProcessingEnv);
        try
        {
            Random random = new(seed);
            StorageTree? tree = null;
            while (!token.IsCancellationRequested)
            {
                // A new tree now and then, so its root comes from the shared cache again.
                if (reads % 64 == 0) tree = new StorageTree(new StorageTrieStoreAdapter(bundle, ContractHash), storageRoot, LimboLogs.Instance);
                UInt256 slot = slots[random.Next(slots.Length)];
                tree!.Get(slot, out UInt256 value);
                UInt256 want = expected.TryGetValue(slot, out UInt256 v) ? v : UInt256.Zero;
                if (value != want) errors.Enqueue($"reader {seed}: slot {slot} read {value}, expected {want}");
                reads++;
            }
        }
        catch (Exception e)
        {
            errors.Enqueue($"reader {seed}: {e.GetType().Name}: {e.Message}");
        }
        finally
        {
            bundle.Dispose();
        }

        return reads;
    }

    /// <summary>
    /// Walks the nodes linked under <paramref name="node"/>: every node found in a slot of a sealed parent must be the node
    /// the parent's bytes refer to there, a resolved node's bytes must hash to its hash, and no unresolved warmer node may
    /// sit in a slot (PR #12980).
    /// </summary>
    /// <returns>The links checked.</returns>
    private static int CheckLinks(TrieNode node, List<string> errors, int depth = 0)
    {
        if (node.NodeType == NodeType.Unknown || node.IsLeaf) return 0;

        INodeData data = node.NodeData!;
        int slots = node.IsBranch ? 16 : 1;
        int linked = 0;
        for (int i = 0; i < slots; i++)
        {
            if (data[i] is not TrieNode child) continue;
            linked++;
            string at = $"depth {depth} slot {i}";
            if (!node.IsDirty && node.FullRlp.IsNotNull)
            {
                Hash256? reference = node.GetChildHash(i);
                if (reference is not null)
                {
                    if (child.Keccak != reference) errors.Add($"{at}: node {child.Keccak} in the slot of {reference}");
                }
                else if (node.GetInlineNodeRlp(i) is not { } inline)
                {
                    errors.Add($"{at}: node {child.Keccak} in an empty slot");
                }
                else if (child.FullRlp.IsNotNull && !child.FullRlp.AsSpan().SequenceEqual(inline))
                {
                    errors.Add($"{at}: embedded node differs from its parent's bytes");
                }
            }

            if (child.NodeType == NodeType.Unknown)
            {
                if ((bool)IsWarmerOwned.GetValue(child)!) errors.Add($"{at}: unresolved warmer node {child.Keccak} in a slot");
            }
            else if (!child.IsDirty && child.Keccak is not null && child.FullRlp.IsNotNull
                     && Keccak.Compute(child.FullRlp.AsSpan()) is var actual && actual != child.Keccak)
            {
                errors.Add($"{at}: bytes of {actual} under {child.Keccak}");
            }

            linked += CheckLinks(child, errors, depth + 1);
        }

        return linked;
    }

    // As PersistentStorageProvider writes them: non-zero values first, deletes after.
    internal static void Write(IWorldStateScopeProvider.IWorldStateWriteBatch batch, Address address, (UInt256 Slot, UInt256 Value)[] changes)
    {
        using IWorldStateScopeProvider.IStorageWriteBatch storageBatch = batch.CreateStorageWriteBatch(address, changes.Length);
        foreach ((UInt256 slot, UInt256 value) in changes.Where(c => !c.Value.IsZero)) storageBatch.Set(slot, value);
        foreach ((UInt256 slot, UInt256 value) in changes.Where(c => c.Value.IsZero)) storageBatch.Set(slot, value);
    }

    private static UInt256 Slot(int index) => new(Keccak.Compute(BitConverter.GetBytes(index)).Bytes, isBigEndian: true);

    /// <summary>The flat prefetch of a real scope, then a check of every link it left, before the bulk write runs.</summary>
    private sealed class InspectingPrefetcher(FlatStorageTree flat) : IStorageTreePrefetcher
    {
        public readonly List<string> Errors = [];
        public int Linked;
        public long Start;
        public long End;

        public void Prefetch(StorageTree tree, ReadOnlySpan<PatriciaTree.BulkSetEntry> entries)
        {
            Start = System.Diagnostics.Stopwatch.GetTimestamp();
            flat.Prefetch(tree, entries);
            End = System.Diagnostics.Stopwatch.GetTimestamp();
            Linked = CheckLinks(tree.RootRef!, Errors);
        }
    }

    /// <summary>A persisted single-contract trie and its next blocks, with a reference trie kept next to it.</summary>
    internal sealed class Chain : IDisposable
    {
        private readonly FlatDbConfig _config = new() { ApplyStorageWritesOnIdleThread = false };
        private readonly ResourcePool _resourcePool;
        private readonly StorageTree _reference = new(new RawScopedTrieStore(new TestMemDb()), LimboLogs.Instance);
        private int _nextNewSlot;

        public readonly RocksDbPersistence Persistence = new(new SnapshotableMemColumnsDb<FlatDbColumns>(), LimboLogs.Instance, NullTrieNodeLog.Instance);
        public readonly Dictionary<UInt256, UInt256> Slots = [];
        public StateId State;
        public Hash256 StorageRoot;

        public Chain(int slots)
        {
            _resourcePool = new ResourcePool(_config);
            _nextNewSlot = slots;
            HandOverTarget target = new();
            using (FlatWorldStateScope scope = new(StateId.PreGenesis,
                       CreateBundle(new NoopPersistenceReader(), new TrieNodeCache(_config, LimboLogs.Instance), ResourcePool.Usage.MainBlockProcessing),
                       new NullCodeDb(), target, _config, new NoopTrieWarmer(), LimboLogs.Instance))
            {
                using (IWorldStateScopeProvider.IWorldStateWriteBatch batch = scope.StartWriteBatch(1))
                {
                    batch.Set(Contract, new Account(1));
                    using IWorldStateScopeProvider.IStorageWriteBatch storageBatch = batch.CreateStorageWriteBatch(Contract, slots);
                    for (int i = 0; i < slots; i++)
                    {
                        UInt256 value = (UInt256)(ulong)(i + 1);
                        storageBatch.Set(Slot(i), value);
                        Slots[Slot(i)] = value;
                    }
                }

                scope.Commit(0);
                State = new StateId(0, scope.RootHash);
                StorageRoot = scope.Get(Contract)!.StorageRoot;
            }

            target.Transient?.ReleaseLease();
            Persist(target.Snapshot ?? throw new InvalidOperationException("The base commit produced no snapshot"));
            foreach ((UInt256 slot, UInt256 value) in Slots) _reference.Set(slot, value.ToMinimalBigEndian());
            _reference.UpdateRootHash();
            Assert.That(_reference.RootHash, Is.EqualTo(StorageRoot), "the base trie");
        }

        public SnapshotBundle CreateBundle(IPersistence.IPersistenceReader reader, TrieNodeCache cache, ResourcePool.Usage usage) =>
            new(new ReadOnlySnapshotBundle(new SnapshotPooledList(0), reader, recordDetailedMetrics: false, PersistedSnapshotStack.Empty()),
                cache, _resourcePool, usage);

        /// <summary>A third deletes, a third updates and a third new slots for <see cref="Kind.Mixed"/>.</summary>
        public (UInt256 Slot, UInt256 Value)[] PickChanges(Kind kind, Random random)
        {
            UInt256[] existing = [.. Slots.Keys.OrderBy(_ => random.Next()).Take(Changes)];
            UInt256 updated = (UInt256)(ulong)random.Next(1, int.MaxValue);
            return kind switch
            {
                Kind.Delete => [.. existing.Select(s => (s, UInt256.Zero))],
                Kind.Update => [.. existing.Select(s => (s, updated))],
                _ =>
                [
                    .. existing[..(Changes / 3)].Select(s => (s, UInt256.Zero)),
                    .. existing[(Changes / 3)..(2 * Changes / 3)].Select(s => (s, updated)),
                    .. Enumerable.Range(0, Changes - 2 * Changes / 3).Select(_ => (Slot(_nextNewSlot++), updated)),
                ],
            };
        }

        /// <summary>Applies <paramref name="changes"/> to the reference and returns its root.</summary>
        public Hash256 Apply((UInt256 Slot, UInt256 Value)[] changes)
        {
            foreach ((UInt256 slot, UInt256 value) in changes)
            {
                if (value.IsZero)
                {
                    Slots.Remove(slot);
                    _reference.Set(slot, []);
                }
                else
                {
                    Slots[slot] = value;
                    _reference.Set(slot, value.ToMinimalBigEndian());
                }
            }

            _reference.UpdateRootHash();
            StorageRoot = _reference.RootHash;
            return StorageRoot;
        }

        public void Persist(Snapshot snapshot)
        {
            using IPersistence.IWriteBatch batch = Persistence.CreateWriteBatch(snapshot.From, snapshot.To, WriteFlags.None);
            foreach (KeyValuePair<HashedKey<Address>, Account?> kv in snapshot.Accounts) batch.SetAccount(kv.Key.Key, kv.Value);
            foreach (KeyValuePair<HashedKey<(Address, UInt256)>, UInt256?> kv in snapshot.Storages) batch.SetStorage(kv.Key.Key.Item1, kv.Key.Key.Item2, kv.Value);
            foreach (KeyValuePair<HashedKey<TreePath>, TrieNode> kv in snapshot.StateNodes) batch.SetStateTrieNode(kv.Key.Key, kv.Value.FullRlp.AsSpan());
            foreach (KeyValuePair<HashedKey<(Hash256, TreePath)>, TrieNode> kv in snapshot.StorageNodes) batch.SetStorageTrieNode(kv.Key.Key.Item1, kv.Key.Key.Item2, kv.Value.FullRlp.AsSpan());
        }

        public void Dispose() => Persistence.Clear();
    }

    /// <summary>
    /// On the prefetch's reader threads only, and once per path, answers a leaf's read with a neighbouring leaf's bytes, as a
    /// path-keyed store holding another version would. Every other read, the commit's included, gets the right bytes.
    /// </summary>
    private sealed class ForeignLeafReader(IPersistence.IPersistenceReader inner, Hash256 address) : IPersistence.IPersistenceReader
    {
        private readonly ConcurrentDictionary<TreePath, bool> _served = new();
        private long _foreign;

        public long ForeignServed => Interlocked.Read(ref _foreign);

        public byte[]? TryLoadStorageRlp(Hash256 addr, in TreePath path, ReadFlags flags)
        {
            byte[]? rlp = inner.TryLoadStorageRlp(addr, path, flags);
            if (rlp is null || path.Length == 0 || addr != address
                || Thread.CurrentThread.Name?.StartsWith("WarmRead", StringComparison.Ordinal) != true
                || !IsLeaf(rlp) || !_served.TryAdd(path, true)) return rlp;

            TreePath other = path;
            int last = path[path.Length - 1];
            for (int n = 1; n < 16; n++)
            {
                other.SetLast((last + n) & 0xf);
                if (inner.TryLoadStorageRlp(addr, other, flags) is { } foreign && IsLeaf(foreign) && !foreign.AsSpan().SequenceEqual(rlp))
                {
                    Interlocked.Increment(ref _foreign);
                    return foreign;
                }
            }

            return rlp;
        }

        private static bool IsLeaf(byte[] rlp)
        {
            TrieNode node = new(NodeType.Unknown, rlp);
            TreePath empty = TreePath.Empty;
            return node.TryResolveNode(NullTrieNodeResolver.Instance, ref empty) && node.IsLeaf;
        }

        public byte[]? TryLoadStateRlp(in TreePath path, ReadFlags flags) => inner.TryLoadStateRlp(path, flags);
        public Account? GetAccount(Address addr) => inner.GetAccount(addr);
        public bool TryGetSlot(Address addr, in UInt256 slot, ref UInt256 outValue) => inner.TryGetSlot(addr, slot, ref outValue);
        public StateId CurrentState => inner.CurrentState;
        public byte[]? GetAccountRaw(in ValueHash256 addrHash) => inner.GetAccountRaw(addrHash);
        public bool TryGetStorageRaw(in ValueHash256 addrHash, in ValueHash256 slotHash, ref UInt256 value) => inner.TryGetStorageRaw(addrHash, slotHash, ref value);
        public IPersistence.IFlatIterator CreateAccountIterator(in ValueHash256 startKey, in ValueHash256 endKey) => inner.CreateAccountIterator(startKey, endKey);
        public IPersistence.IFlatIterator CreateStorageIterator(in ValueHash256 accountKey, in ValueHash256 startSlotKey, in ValueHash256 endSlotKey) => inner.CreateStorageIterator(accountKey, startSlotKey, endSlotKey);
        public bool IsPreimageMode => inner.IsPreimageMode;
        public void Dispose() => inner.Dispose();
    }

    /// <summary>Gives every storage-node read a few microseconds of random delay, so racing walkers interleave.</summary>
    private sealed class JitterReader(IPersistence.IPersistenceReader inner, int seed) : IPersistence.IPersistenceReader
    {
        private readonly ThreadLocal<Random> _random = new(() => new Random(seed ^ Environment.CurrentManagedThreadId));

        public byte[]? TryLoadStorageRlp(Hash256 address, in TreePath path, ReadFlags flags)
        {
            Thread.SpinWait(_random.Value!.Next(0, 2_000));
            if (_random.Value!.Next(8) == 0) Thread.Yield();
            return inner.TryLoadStorageRlp(address, path, flags);
        }

        public byte[]? TryLoadStateRlp(in TreePath path, ReadFlags flags) => inner.TryLoadStateRlp(path, flags);
        public Account? GetAccount(Address address) => inner.GetAccount(address);
        public bool TryGetSlot(Address address, in UInt256 slot, ref UInt256 outValue) => inner.TryGetSlot(address, slot, ref outValue);
        public StateId CurrentState => inner.CurrentState;
        public byte[]? GetAccountRaw(in ValueHash256 addrHash) => inner.GetAccountRaw(addrHash);
        public bool TryGetStorageRaw(in ValueHash256 addrHash, in ValueHash256 slotHash, ref UInt256 value) => inner.TryGetStorageRaw(addrHash, slotHash, ref value);
        public IPersistence.IFlatIterator CreateAccountIterator(in ValueHash256 startKey, in ValueHash256 endKey) => inner.CreateAccountIterator(startKey, endKey);
        public IPersistence.IFlatIterator CreateStorageIterator(in ValueHash256 accountKey, in ValueHash256 startSlotKey, in ValueHash256 endSlotKey) => inner.CreateStorageIterator(accountKey, startSlotKey, endSlotKey);
        public bool IsPreimageMode => inner.IsPreimageMode;
        public void Dispose()
        {
            inner.Dispose();
            _random.Dispose();
        }
    }

    internal sealed class HandOverTarget : IFlatCommitTarget
    {
        public Snapshot? Snapshot { get; private set; }
        public TransientResource? Transient { get; private set; }

        public void AddSnapshot(Snapshot snapshot, TransientResource transientResource)
        {
            Snapshot = snapshot;
            Transient = transientResource;
        }
    }

    internal sealed class NullCodeDb : IWorldStateScopeProvider.ICodeDb
    {
        public ReadOnlyMemory<byte> GetCode(in ValueHash256 codeHash) => default;

        public IWorldStateScopeProvider.ICodeSetter BeginCodeWrite() => new NullCodeSetter();

        private sealed class NullCodeSetter : IWorldStateScopeProvider.ICodeSetter
        {
            public void Set(in ValueHash256 codeHash, ReadOnlySpan<byte> code) { }

            public void Dispose() { }
        }
    }
}

/// <summary>
/// Verifier (xp2v): which scopes prefetch. <see cref="FlatOverridableWorldScope"/> builds its scopes, used for calls,
/// simulations, traces, proofs and Flashbots validation, with a <see cref="ResourcePool.Usage.ReadOnlyProcessingEnv"/>
/// bundle, <c>isReadOnly</c> left false and no reader pool. The second test fails on e4e68c33ca: such a scope prefetches.
/// </summary>
public class PrefetchScopeUsageTests
{
    [TestCase(ResourcePool.Usage.MainBlockProcessing, 1)]
    [TestCase(ResourcePool.Usage.ReadOnlyProcessingEnv, 0)]
    public void Only_main_block_processing_prefetches(ResourcePool.Usage usage, int expectedContracts)
    {
        using StorageRootPrefetchVerifyTests.Chain chain = new(5_000);
        (UInt256 Slot, UInt256 Value)[] changes = chain.PickChanges(StorageRootPrefetchVerifyTests.Kind.Mixed, new Random(5));
        FlatDbConfig config = new() { ApplyStorageWritesOnIdleThread = false, StorageRootPrefetchMinChanges = 1 };
        SnapshotBundle bundle = chain.CreateBundle(chain.Persistence.CreateReader(), new TrieNodeCache(config, LimboLogs.Instance), usage);
        // As FlatOverridableWorldScope.OverridableFlatScopeProvider.TryBeginScope builds it: no reader pool, isReadOnly default.
        using FlatWorldStateScope scope = new(chain.State, bundle, new StorageRootPrefetchVerifyTests.NullCodeDb(),
            new StorageRootPrefetchVerifyTests.HandOverTarget(), config, new NoopTrieWarmer(), LimboLogs.Instance);
        using (IWorldStateScopeProvider.IWorldStateWriteBatch batch = scope.StartWriteBatch(1))
        {
            StorageRootPrefetchVerifyTests.Write(batch, StorageRootPrefetchVerifyTests.Contract, changes);
        }

        Assert.That(scope.StorageRootPrefetchCounts.Contracts, Is.EqualTo(expectedContracts));
    }
}
