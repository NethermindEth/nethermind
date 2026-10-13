// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.IO;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Logging;
using Nethermind.Trie;
using NUnit.Framework;
using static Nethermind.State.Flat.Rust.Test.ForkTestChain;

namespace Nethermind.State.Flat.Rust.Test;

/// <summary>
/// The trie warmer's reads of a forked <see cref="Fork.SnapshotBundle"/>: while a thread holds the bundle's read-only
/// lease, as every warmer traversal does, its loads read only the read-only bundle, the in-memory snapshots below the
/// scope and the persistence, as the C# warmers do; a warmer miss never reaches the trie node cache, a mismatching
/// node never poisons the live reads, a read after disposal fails cleanly, and retirement waits for a read in flight.
/// </summary>
[Parallelizable(ParallelScope.All)]
public class ForkSnapshotBundleWarmerTests
{
    private static readonly TimeSpan BailOutTimeout = TimeSpan.FromSeconds(30);
    private static readonly byte[] WrittenRlp = Bytes.FromHexString("0xc3820909");
    private static readonly Hash256 WrittenHash = Keccak.Compute("written");

    [Test]
    public async Task Loads_under_a_warmer_lease_read_below_the_scope_only([Values] bool storage, [Values] bool committed)
    {
        NativeTestSnapshots.Require();
        await using Chain chain = await Chain.Open();
        using Fork.SnapshotBundle bundle = chain.Gather(chain.Cache);
        TrieNode written = new(NodeType.Leaf, WrittenHash, WrittenRlp);
        if (storage) bundle.SetStorageNode(PersistedOwner, PersistedPath, written);
        else bundle.SetStateNode(PersistedPath, written);
        if (committed) bundle.CollectAndApplySnapshot(chain.Head, new StateId(3, Keccak.Compute("3")), returnSnapshot: false);

        string? live = Load(bundle, storage);
        Assert.That(bundle.TryLeaseReadOnlyBundle(), Is.True);
        string? leased = Load(bundle, storage);
        string? onAnotherThread = Task.Run(() => Load(bundle, storage)).Result;
        bundle.ReleaseReadOnlyBundleLease();
        string? released = Load(bundle, storage);

        Assert.That(new[] { live, leased, onAnotherThread, released }, Is.EqualTo(new[] { WrittenRlp.ToHexString(), PersistedRlp.ToHexString(), WrittenRlp.ToHexString(), WrittenRlp.ToHexString() }));

        static string? Load(Fork.SnapshotBundle bundle, bool storage) =>
            (storage ? bundle.TryLoadStorageRlp(PersistedOwner, PersistedPath, WrittenHash, ReadFlags.None) : bundle.TryLoadStateRlp(PersistedPath, WrittenHash, ReadFlags.None))?.ToHexString();
    }

    /// <summary>
    /// The early storage apply builds its tree on the idle thread from below the scope only: a storage root node of
    /// another RLP that an earlier commit of the scope holds at the path of the genesis one, which the live reads see,
    /// never reaches it.
    /// </summary>
    [Test]
    public async Task Early_storage_apply_reads_below_the_scope_only()
    {
        RustNativeLibrary.Require();
        TimeSpan minIdleGap = Fork.IdleStorageApplier.MinIdleGap;
        Fork.IdleStorageApplier.MinIdleGap = TimeSpan.Zero;
        try
        {
            FlatDbConfig config = FlatManagerContainer.Config(FlatDbBackend.Rust);
            config.ApplyStorageWritesOnIdleThread = true;
            await using WorldStateNode node = new(config);
            IWorldStateScopeProvider provider = node.WorldStateManager.GlobalWorldState;
            Address address = TestItem.AddressA;
            Hash256 addressHash = address.ToAccountPath.ToCommitment();
            BlockHeader genesis = WorldStateNode.Commit(provider, null, 0, WorldStateNode.Write(address, new Account(1, 1), false, (1, 1), (2, 2)));

            Assert.That(provider.TryBeginScope(genesis, new LocalMetrics(), out IWorldStateScopeProvider.IScope? begun), Is.True);
            using Fork.FlatWorldStateScope scope = (Fork.FlatWorldStateScope)begun!;
            Fork.SnapshotBundle bundle = SnapshotBundleOf(scope);
            Hash256 storageRoot = scope.Get(address)!.StorageRoot;
            bundle.SetStorageNode(addressHash, TreePath.Empty, new TrieNode(NodeType.Leaf, storageRoot, WrittenRlp));
            // Moves the node out of the transient resource into the scope's own snapshots, and leaves no cached root.
            bundle.CollectAndApplySnapshot(new StateId(genesis), new StateId(genesis), returnSnapshot: false);
            node.Resolve<ITrieNodeCache>().Clear();
            Fork.FlatStorageTree storageTree = (Fork.FlatStorageTree)scope.CreateStorageTree(address);
            storageTree.HintSet(3, 3);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(scope.AppliesStorageWritesEarly, Is.True);
                Assert.That(() => storageTree.EarlyWritesDrained, Is.True.After((int)BailOutTimeout.TotalMilliseconds, 10));
                Assert.That(scope.EarlyApplyCounts.Applied, Is.EqualTo(1), "the early tree resolved the persisted root");
                Assert.That(bundle.TryLoadStorageRlp(addressHash, TreePath.Empty, storageRoot, ReadFlags.None), Is.EqualTo(WrittenRlp), "the live read sees the written root");
            }
        }
        finally
        {
            Fork.IdleStorageApplier.MinIdleGap = minIdleGap;
        }
    }

    /// <summary>
    /// The warmer adapter rejects a node whose RLP has another hash as a miss, after which the live read still finds
    /// the node; a miss of the warmer is never handed to the trie node cache, nor read as the live node.
    /// </summary>
    [Test]
    public async Task Warmer_misses_and_mismatches_leave_the_live_reads_and_the_cache_alone([Values] bool storage)
    {
        NativeTestSnapshots.Require();
        await using Chain chain = await Chain.Open();
        using Fork.SnapshotBundle bundle = chain.Gather(chain.Cache);
        Trie.Pruning.ITrieNodeResolver warmer = storage
            ? new Fork.StorageTrieStoreWarmerAdapter(bundle, PersistedOwner)
            : new Fork.StateTrieStoreWarmerAdapter(bundle);
        Hash256 missing = Keccak.Compute("missing");

        Assert.That(bundle.TryLeaseReadOnlyBundle(), Is.True);
        byte[]? mismatching = warmer.TryLoadRlp(PersistedPath, PersistedHash);
        byte[]? matching = warmer.TryLoadRlp(PersistedPath, Keccak.Compute(PersistedRlp));
        TrieNode warmedMiss = warmer.FindCachedOrUnknown(InMemoryPath, missing);
        bundle.ReleaseReadOnlyBundleLease();
        byte[]? live = storage
            ? bundle.TryLoadStorageRlp(PersistedOwner, PersistedPath, PersistedHash, ReadFlags.None)
            : bundle.TryLoadStateRlp(PersistedPath, PersistedHash, ReadFlags.None);
        TrieNode liveMiss = storage
            ? bundle.FindStorageNodeOrUnknown(PersistedOwner, InMemoryPath, missing)
            : bundle.FindStateNodeOrUnknown(InMemoryPath, missing);
        (Fork.Snapshot? snapshot, TransientResource? retired) = bundle.CollectAndApplySnapshot(chain.Head, new StateId(3, Keccak.Compute("3")));
        snapshot!.Dispose();
        chain.Cache.Add(retired!);
        TestInternals.ReleaseLease(retired!);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(mismatching, Is.Null, "a node of another hash reads as a warmer miss");
            Assert.That(matching, Is.EqualTo(PersistedRlp));
            Assert.That(live, Is.EqualTo(PersistedRlp), "the live read is not poisoned");
            Assert.That(liveMiss, Is.Not.SameAs(warmedMiss));
            Assert.That(liveMiss.NodeType, Is.EqualTo(NodeType.Unknown));
            Assert.That(chain.Cache.TryGet(storage ? PersistedOwner : null, InMemoryPath, missing, out _), Is.False);
        }
    }

    /// <summary>
    /// A warmer read after disposal never touches a recycled resource: a find falls back to the read-only bundle and a
    /// load fails with <see cref="ObjectDisposedException"/>, never a null reference.
    /// </summary>
    [Test]
    public async Task A_warmer_read_after_disposal_falls_back_or_fails_as_disposed()
    {
        NativeTestSnapshots.Require();
        await using Chain chain = await Chain.Open();
        Fork.SnapshotBundle bundle = chain.Gather(chain.Cache);
        Assert.That(bundle.TryLeaseReadOnlyBundle(), Is.True);
        try
        {
            bundle.Dispose();
            Task<(TrieNode Node, bool ShouldPrewarm)> read = Task.Run(() =>
                (bundle.FindStateNodeOrUnknownForTrieWarmer(PersistedPath, PersistedHash), bundle.ShouldQueuePrewarm(Persisted)));

            using (Assert.EnterMultipleScope())
            {
                Assert.That(read.Wait(BailOutTimeout), Is.True, "the warmer read returned on a disposed bundle");
                Assert.That((read.Result.Node.NodeType, read.Result.ShouldPrewarm), Is.EqualTo((NodeType.Unknown, false)));
                Assert.That(() => bundle.TryLoadStateRlp(PersistedPath, PersistedHash, ReadFlags.None), Throws.TypeOf<ObjectDisposedException>());
            }
        }
        finally
        {
            bundle.ReleaseReadOnlyBundleLease();
        }
    }

    /// <summary>Retirement must not scan the transient while a warmer read that passed the lease check still writes to it.</summary>
    [Test]
    public async Task Retirement_waits_for_an_in_flight_warmer_read()
    {
        NativeTestSnapshots.Require();
        await using Chain chain = await Chain.Open();
        using ManualResetEventSlim readEntered = new(false);
        using ManualResetEventSlim releaseRead = new(false);
        using Fork.SnapshotBundle bundle = chain.Gather(new BlockingTrieNodeCache(readEntered, releaseRead));

        Task<TrieNode> warmerRead = Task.Run(() => bundle.FindStateNodeOrUnknownForTrieWarmer(InMemoryPath, Keccak.Compute("missing")));
        Assert.That(readEntered.Wait(BailOutTimeout), Is.True, "the warmer read did not reach the cache");
        (Fork.Snapshot? snapshot, TransientResource? retired) = bundle.CollectAndApplySnapshot(chain.Head, new StateId(3, Keccak.Compute("3")));
        snapshot!.Dispose();
        Task add = Task.Run(() => chain.Cache.Add(retired!));
        bool addCompletedWhileRead = add.Wait(TimeSpan.FromMilliseconds(200));
        releaseRead.Set();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(addCompletedWhileRead, Is.False, "retirement scanned the transient under an in-flight warmer read");
            Assert.That(add.Wait(BailOutTimeout), Is.True, "retirement did not resume after the warmer read released its lease");
            Assert.That(warmerRead.Wait(BailOutTimeout), Is.True);
            Assert.That(warmerRead.Result.NodeType, Is.EqualTo(NodeType.Unknown));
        }

        TestInternals.ReleaseLease(retired!);
    }

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_snapshotBundle")]
    private static extern ref Fork.SnapshotBundle SnapshotBundleOf(Fork.FlatWorldStateScope scope);

    /// <summary>The chain of <see cref="ForkTestChain"/> on the Rust backend, and a trie node cache of its own.</summary>
    private sealed class Chain(TempPath path, FlatManagerContainer container, StateId head) : IAsyncDisposable
    {
        public TrieNodeCache Cache { get; } = new(new FlatDbConfig { TrieCacheMemoryBudget = 1024 * 1024 }, LimboLogs.Instance);

        public StateId Head => head;

        public static async Task<Chain> Open()
        {
            TempPath path = TempPath.GetTempDirectory();
            FlatManagerContainer container = new(FlatManagerContainer.Config(FlatDbBackend.Rust), path.Path, new TestFinality());
            try
            {
                return new Chain(path, container, Build(container, clearInMemory: false)[2]);
            }
            catch
            {
                await container.DisposeAsync();
                path.Dispose();
                throw;
            }
        }

        /// <summary>A bundle on the head over its read-only bundle, reading through <paramref name="cache"/>.</summary>
        public Fork.SnapshotBundle Gather(ITrieNodeCache cache) =>
            new(container.Resolve<Fork.IFlatDbManager>().GatherReadOnlySnapshotBundle(head), cache, container.ResourcePool, ResourcePool.Usage.MainBlockProcessing);

        public async ValueTask DisposeAsync()
        {
            await container.DisposeAsync();
            path.Dispose();
        }
    }

    private sealed class BlockingTrieNodeCache(ManualResetEventSlim entered, ManualResetEventSlim release) : ITrieNodeCache
    {
        public bool TryGet(Hash256? address, in TreePath path, Hash256 hash, [NotNullWhen(true)] out TrieNode? node)
        {
            entered.Set();
            if (!release.Wait(BailOutTimeout)) throw new TimeoutException("warmer read was not released");
            node = null;
            return false;
        }

        public void Add(TransientResource transientResource) { }

        public void Clear() { }
    }
}
