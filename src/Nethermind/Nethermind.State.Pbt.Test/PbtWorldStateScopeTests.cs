// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Autofac;
using Nethermind.Core.Memory;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Blockchain;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Pbt;
using Nethermind.State.Flat;
using Nethermind.State.Pbt.ScopeProvider;
using Nethermind.State.Pbt.Persistence;
using NUnit.Framework;
using RefCountingMemoryMetrics = Nethermind.Core.Memory.Metrics.Metrics;
using NSubstitute;

namespace Nethermind.State.Pbt.Test;

public class PbtWorldStateScopeTests
{
    [Test]
    public async Task Override_bundles_use_the_production_cache_without_changing_canonical_state([Values] bool delete)
    {
        PbtConfig config = new() { Enabled = true };
        await using IContainer container = PbtTestContext.BuildProductionContainer(config);
        PbtWorldStateManager manager = container.Resolve<PbtWorldStateManager>();
        PbtTrieNodeCache cache = container.Resolve<PbtTrieNodeCache>();
        Hash256 canonicalRoot;
        using (IWorldStateScopeProvider.IScope scope = manager.GlobalWorldState.BeginScope(null, new LocalMetrics()))
        {
            Write(scope, 1);
            scope.Commit(1);
            canonicalRoot = scope.RootHash;
        }
        BlockHeader parent = Build.A.BlockHeader.WithNumber(1).WithStateRoot(canonicalRoot).TestObject;
        PbtNodePath rootPath = new([], 0);
        // The canonical commit hands its folded groups to the manager, which folds them into the shared cache in the background.
        Assert.That(() => cache.TryGet(canonicalRoot.ValueHash256, rootPath, out _), Is.True.After(5000, 10));
        using IOverridableWorldScope overrides = manager.CreateOverridableWorldScope();
        using (PbtWorldStateScope scope = (PbtWorldStateScope)overrides.WorldState.BeginScope(parent, new LocalMetrics()))
        {
            using RefCountingMemory? group = scope.Bundle.GetNodeGroup(rootPath.ToPath<PbtStorageNodePath>(), canonicalRoot.ValueHash256);
            Assert.That(group, Is.Not.Null);
            using RefCountingMemory? cached = cache.TryGet(canonicalRoot.ValueHash256, rootPath, out RefCountingMemory? payload) ? payload : null;
            Assert.That(cached!.Memory.ToArray(), Is.EqualTo(group!.Memory.ToArray()));
            using (IWorldStateScopeProvider.IWorldStateWriteBatch batch = scope.StartWriteBatch(1))
                batch.Set(TestItem.AddressA, delete ? null : Build.An.Account.WithBalance(2).TestObject);
            scope.Commit(2);
            Assert.That(scope.RootHash, Is.Not.EqualTo(canonicalRoot));
            Assert.That(cache.TryGet(scope.RootHash.ValueHash256, rootPath, out _), Is.False, "an override commit must not feed the shared cache");
        }
        using IWorldStateScopeProvider.IScope canonical = manager.GlobalWorldState.BeginScope(parent, new LocalMetrics());
        using (Assert.EnterMultipleScope())
        {
            Assert.That(canonical.RootHash, Is.EqualTo(canonicalRoot));
            Assert.That(canonical.Get(TestItem.AddressA)!.Balance, Is.EqualTo((UInt256)1));
        }
    }

    [Test]
    public async Task Header_root_substitution_requires_explicit_opt_in([Values] bool fakeMatchingStateRoot)
    {
        PbtConfig config = new() { Enabled = true };
        if (fakeMatchingStateRoot) config.FakeMatchingStateRoot = true;
        await using IContainer container = PbtTestContext.BuildProductionContainer(config);
        PbtWorldStateManager manager = container.Resolve<PbtWorldStateManager>();
        IBlockTree blockTree = container.Resolve<IBlockTree>();
        Hash256 genesisRoot;
        using (IWorldStateScopeProvider.IScope genesisScope = manager.GlobalWorldState.BeginScope(null, new LocalMetrics()))
        {
            Write(genesisScope, 1);
            genesisScope.Commit(0);
            genesisRoot = genesisScope.RootHash;
        }
        Block genesis = Build.A.Block.Genesis.WithStateRoot(genesisRoot).TestObject;
        blockTree.SuggestBlock(genesis);
        Block child = Build.A.Block.WithParent(genesis).WithStateRoot(TestItem.KeccakA).TestObject;
        blockTree.SuggestBlock(child);

        Hash256 computedRoot;
        using (IOverridableWorldScope overrides = manager.CreateOverridableWorldScope())
        using (IWorldStateScopeProvider.IScope overrideScope = overrides.WorldState.BeginScope(genesis.Header, new LocalMetrics()))
        {
            Write(overrideScope, 2);
            overrideScope.UpdateRootHash();
            computedRoot = overrideScope.RootHash;
        }

        using IWorldStateScopeProvider.IScope scope = manager.GlobalWorldState.BeginScope(genesis.Header, new LocalMetrics());
        Write(scope, 2);
        scope.Commit(1);
        using PbtSnapshotBundle bundle = container.Resolve<IPbtDbManager>().GatherBundle(
            new StateId(1, scope.RootHash), PbtResourcePool.Usage.ReadOnlyProcessingEnv);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(config.FakeMatchingStateRoot, Is.EqualTo(fakeMatchingStateRoot));
            Assert.That(container.Resolve<IPbtChildHeaderSource>(), Is.TypeOf(
                fakeMatchingStateRoot ? typeof(PbtBlockTreeChildHeaderSource) : typeof(NullPbtChildHeaderSource)));
            Assert.That(computedRoot, Is.Not.EqualTo(child.StateRoot));
            Assert.That(scope.RootHash, Is.EqualTo(fakeMatchingStateRoot ? child.StateRoot : computedRoot));
            Assert.That(bundle.TreeRoot, Is.EqualTo(computedRoot.ValueHash256));
            Assert.That(bundle.GetAccount(TestItem.AddressA)!.Balance, Is.EqualTo((UInt256)2));
        }
    }

    [Test]
    public async Task Lifecycle_logging_respects_debug_level([Values] bool debugEnabled)
    {
        InterfaceLogger logger = Substitute.For<InterfaceLogger>();
        logger.IsDebug.Returns(debugEnabled);
        List<string> messages = [];
        logger.When(log => log.Debug(Arg.Any<string>())).Do(call => messages.Add(call.Arg<string>()));
        await using PbtTestContext ctx = new();
        IWorldStateScopeProvider.IScope scope = ctx.CreateScopeProvider(logManager: new OneLoggerLogManager(new ILogger(logger)))
            .BeginScope(null, new LocalMetrics());
        using (scope)
        {
            Write(scope, 1);
            scope.Commit(0);
        }
        scope.Dispose();

        string[] stages = ["opened", "commit begin", "root calculation begin", "root calculated", "commit completed", "close begin", "closed"];
        Assert.That(messages, Has.Count.EqualTo(debugEnabled ? stages.Length : 0));
        using (Assert.EnterMultipleScope())
        {
            for (int index = 0; index < messages.Count; index++)
            {
                Assert.That(messages[index], Does.Contain(stages[index]));
                Assert.That(messages[index], Does.Contain("managedBytes="));
                Assert.That(messages[index], Does.Contain("state="));
            }
        }
    }

    /// <summary>
    /// A read-only scope is read-only with respect to the repository, not to itself: it processes and
    /// commits locally like any other, and only keeps the result to itself.
    /// </summary>
    [Test]
    public async Task ReadOnlyScope_CommitsLocally_WithoutPublishingToTheRepository()
    {
        await using PbtTestContext ctx = new();
        IWorldStateScopeProvider provider = ctx.WorldStateManager.CreateResettableWorldState();

        using IWorldStateScopeProvider.IScope scope = provider.BeginScope(null, new LocalMetrics());
        Write(scope, 1);

        scope.UpdateRootHash();
        scope.Commit(1);

        Assert.That(scope.Get(TestItem.AddressA)?.Balance, Is.EqualTo(UInt256.One), "the scope reads back what it committed");
        Assert.That(ctx.Repository.Count, Is.Zero, "and the layer never reaches the repository");
    }

    [Test]
    [NonParallelizable]
    public async Task Slab_backed_node_groups_are_freed_once_the_scope_and_cache_are_gone()
    {
        long baselineCount = RefCountingMemoryMetrics.ActiveNativeRefCountingMemoryCount;
        using SlabRefCountingMemoryProvider slab = PbtNodeGroupMemory.CreateSlabProvider();
        await using (PbtTestContext ctx = new(nodeGroupMemory: slab))
        {
            using IWorldStateScopeProvider.IScope scope = ctx.BeginScope(null);
            Write(scope, 1);
            scope.UpdateRootHash();
            scope.Commit(1);
            Assert.That(RefCountingMemoryMetrics.ActiveNativeRefCountingMemoryCount, Is.GreaterThan(baselineCount), "the fold wrote its groups into native memory");
        }

        // Regions freed on fold and persistence threads stay parked in their caches, so only the
        // instance count proves that every payload was released.
        Assert.That(RefCountingMemoryMetrics.ActiveNativeRefCountingMemoryCount, Is.EqualTo(baselineCount));
    }

    [TestCase(7u, TestName = "header slot, on the account's own stem")]
    [TestCase(1000u, TestName = "storage-zone slot, on a stem of its own")]
    public async Task DeletedAccount_RemovesAccountAndStorage(uint slot)
    {
        await using PbtTestContext ctx = new();
        using IWorldStateScopeProvider.IScope scope = ctx.BeginScope(null);

        using (IWorldStateScopeProvider.IWorldStateWriteBatch batch = scope.StartWriteBatch(1))
        {
            batch.Set(TestItem.AddressA, Build.An.Account.WithBalance(1).WithNonce(2).TestObject);
            using IWorldStateScopeProvider.IStorageWriteBatch storage = batch.CreateStorageWriteBatch(TestItem.AddressA, 1);
            storage.Set(slot, (UInt256)0xAB);
        }

        scope.Commit(0);
        using (IWorldStateScopeProvider.IWorldStateWriteBatch batch = scope.StartWriteBatch(1))
        {
            batch.Set(TestItem.AddressA, null);
        }
        scope.Commit(1);

        Assert.That(scope.Get(TestItem.AddressA), Is.Null);
        Assert.That(scope.CreateStorageTree(TestItem.AddressA).Get(slot), Is.EqualTo(UInt256.Zero));
    }

    [Test]
    public async Task WholeCode_IsRetainedByBundle_AfterCommit([Values] bool writeAccount)
    {
        byte[] code = Bytes.FromHexString("6001");
        Hash256 codeHash = Keccak.Compute(code);
        await using PbtTestContext ctx = new();
        ctx.CodeDb[codeHash.Bytes] = code;
        using PbtWorldStateScope scope = ctx.BeginScope(null);
        scope.Commit(0);

        // Code the code DB already holds must still be written, or its chunk leaves never enter the tree.
        Assert.That(scope.CodeDb.ContainsCode(codeHash.ValueHash256), Is.False);
        using (IWorldStateScopeProvider.ICodeSetter codeWriter = scope.CodeDb.BeginCodeWrite())
            codeWriter.Set(codeHash.ValueHash256, code);
        Assert.That(scope.Bundle.GetCode(codeHash.ValueHash256)!.Code.ToArray(), Is.EqualTo(code));

        // The balance moves the root either way, so the code is sealed into a snapshot even when no account takes it.
        Account accountA = writeAccount ? Build.An.Account.WithBalance(1).WithCode(code).TestObject : Build.An.Account.WithBalance(1).TestObject;
        using (IWorldStateScopeProvider.IWorldStateWriteBatch batch = scope.StartWriteBatch(1))
            batch.Set(TestItem.AddressA, accountA);
        scope.Commit(0);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(scope.Bundle.GetCode(codeHash.ValueHash256)!.Code.ToArray(), Is.EqualTo(code));
            Assert.That(scope.CodeDb.GetCode(codeHash.ValueHash256).ToArray(), Is.EqualTo(code));
            Assert.That(scope.CodeDb.ContainsCode(codeHash.ValueHash256), Is.True);
        }

        // The world state skips the code write on ContainsCode, so a later deployment must stage the chunk leaves
        // itself: without an account in the first block, they never entered the tree.
        using (IWorldStateScopeProvider.IWorldStateWriteBatch batch = scope.StartWriteBatch(1))
            batch.Set(TestItem.AddressB, Build.An.Account.WithCode(code).TestObject);
        scope.Commit(1);

        List<RebuildEntry> expected = [];
        PbtTestLeaves.AddAccount(expected, TestItem.AddressA, accountA, writeAccount ? code : null);
        PbtTestLeaves.AddAccount(expected, TestItem.AddressB, Build.An.Account.WithCode(code).TestObject, code);
        Assert.That(scope.RootHash.Bytes.ToArray(), Is.EqualTo(ReferenceRoot(expected.DistinctBy(entry => entry.Key).ToDictionary(entry => entry.Key, entry => entry.Leaf))));
    }

    [TestCase(7u, false)]
    [TestCase(1000u, false)]
    [TestCase(1000u, true)]
    public async Task RootUpdates_PreservePartitionLeavesThroughRepeatedFoldsAndCommit(uint updatedSlot, bool codeAfterAccount)
    {
        byte[] code = new byte[(256 + 2) * PbtKeyDerivation.CodeChunkSize];
        Array.Fill(code, (byte)0x01);
        Hash256 codeHash = Keccak.Compute(code);
        await using PbtTestContext ctx = new();
        using PbtWorldStateScope scope = ctx.BeginScope(null);
        if (!codeAfterAccount) WriteCode();
        using (IWorldStateScopeProvider.IWorldStateWriteBatch batch = scope.StartWriteBatch(1))
        {
            batch.Set(TestItem.AddressA, Build.An.Account.WithBalance(1).WithCode(code).TestObject);
            using IWorldStateScopeProvider.IStorageWriteBatch storage = batch.CreateStorageWriteBatch(TestItem.AddressA, 2);
            storage.Set(7, (UInt256)0xab);
            storage.Set(1000, (UInt256)0xcd);
        }

        if (codeAfterAccount) WriteCode();
        Assert.That(scope.CreateStorageTree(TestItem.AddressA).Get(7), Is.EqualTo((UInt256)0xab));
        scope.UpdateRootHash();
        Assert.That(scope.Bundle.PendingMutationCount, Is.Zero);
        Hash256 initialRoot = scope.RootHash;
        scope.UpdateRootHash();
        Assert.That(scope.RootHash, Is.EqualTo(initialRoot));

        using (IWorldStateScopeProvider.IWorldStateWriteBatch batch = scope.StartWriteBatch(1))
        using (IWorldStateScopeProvider.IStorageWriteBatch storage = batch.CreateStorageWriteBatch(TestItem.AddressA, 1))
            storage.Set(updatedSlot, (UInt256)0xef);
        scope.Get(TestItem.AddressB);
        Assert.That(scope.Bundle.PendingMutationCount, Is.EqualTo(1));
        scope.UpdateRootHash();
        Assert.That(scope.Bundle.PendingMutationCount, Is.Zero);
        scope.Commit(0);

        using PbtReadOnlySnapshotBundle reopened = ((IPbtDbManager)ctx.Manager).GatherReadOnlyBundle(new StateId(0, scope.RootHash));
        List<RebuildEntry> expected = [];
        PbtTestLeaves.AddAccount(expected, TestItem.AddressA, Build.An.Account.WithBalance(1).WithCode(code).TestObject, code);
        PbtTestLeaves.AddSlot(expected, TestItem.AddressA, 7, (UInt256)(updatedSlot == 7 ? 0xef : 0xab));
        PbtTestLeaves.AddSlot(expected, TestItem.AddressA, 1000, (UInt256)(updatedSlot == 1000 ? 0xef : 0xcd));
        Dictionary<PbtVariableTreeKey, ValueHash256> leaves = expected.ToDictionary(entry => entry.Key, entry => entry.Leaf);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(leaves.ContainsKey((PbtVariableTreeKey)PbtStateKey.Code(codeHash.ValueHash256, 256)), Is.True);
            Assert.That(scope.RootHash.Bytes.ToArray(), Is.EqualTo(ReferenceRoot(leaves)));
            Assert.That(reopened.TreeRoot, Is.EqualTo(scope.RootHash.ValueHash256));
        }
        void WriteCode()
        {
            using IWorldStateScopeProvider.ICodeSetter codeWriter = scope.CodeDb.BeginCodeWrite();
            codeWriter.Set(codeHash.ValueHash256, code);
        }
    }

    [Test]
    public async Task Parallel_storage_writes_and_abandoned_scope_do_not_contaminate_reused_builder([Values] bool foldBeforeAbandon)
    {
        await using PbtTestContext ctx = new();
        using (PbtWorldStateScope abandoned = ctx.BeginScope(null))
        {
            using IWorldStateScopeProvider.IWorldStateWriteBatch batch = abandoned.StartWriteBatch(0);
            Parallel.For(0, 32, index =>
            {
                using IWorldStateScopeProvider.IStorageWriteBatch storage = batch.CreateStorageWriteBatch(TestItem.AddressA, 1);
                storage.Set((UInt256)(uint)(1000 + index), (UInt256)0xab);
            });
            Assert.That(abandoned.Bundle.PendingMutationCount, Is.EqualTo(32));
            if (foldBeforeAbandon) abandoned.UpdateRootHash();
            for (uint index = 0; index < 32; index++)
                Assert.That(abandoned.CreateStorageTree(TestItem.AddressA).Get(1000 + index), Is.EqualTo((UInt256)0xab));
        }
        using PbtWorldStateScope reused = ctx.BeginScope(null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(reused.Bundle.PendingMutationCount, Is.Zero);
            Assert.That(reused.CreateStorageTree(TestItem.AddressA).Get(1000), Is.EqualTo(UInt256.Zero));
        }
    }

    [Test]
    public async Task Account_reads_are_promoted_into_the_snapshot_and_hints_are_not()
    {
        await using PbtTestContext ctx = new();
        using PbtWorldStateScope scope = ctx.BeginScope(null);
        Account hinted = Build.An.Account.WithBalance(1).TestObject;
        scope.Get(TestItem.AddressA);
        scope.HintGet(TestItem.AddressA, hinted);
        scope.HintGet(TestItem.AddressB, hinted);
        scope.Get(TestItem.AddressB);

        using PbtSnapshot snapshot = scope.Bundle.CollectSnapshot(StateId.PreGenesis, new StateId(1, default), default);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(snapshot.Content.Accounts.TryGetValue(PbtStateKey.AddressKeyHash(TestItem.AddressA), out PbtAccount? readA), Is.True, "a read is promoted");
            Assert.That(readA, Is.Null, "a later hint never shadows a promoted read");
            Assert.That(snapshot.Content.Accounts.ContainsKey(PbtStateKey.AddressKeyHash(TestItem.AddressB)), Is.False,
                "a hint is never promoted, and a read served by a hint never promotes it");
        }
    }

    [Test]
    public async Task Cache_preserves_actual_roots_across_forks_deletion_and_reopen([Values(1UL, 1048576UL)] ulong cacheBudget)
    {
        List<ValueHash256> expected = await Run(0);
        List<ValueHash256> actual = await Run(cacheBudget);
        Assert.That(actual, Is.EqualTo(expected));

        static async Task<List<ValueHash256>> Run(ulong budget)
        {
            SnapshotableMemColumnsDb<PbtColumns> database = new("pbt-cache-parity");
            PbtConfig config = new()
            {
                AccountTrieNodeCacheSizeBudget = budget,
                CodeTrieNodeCacheSizeBudget = budget,
                StorageTrieNodeCacheSizeBudget = budget,
                CompactSize = 2,
            };
            List<ValueHash256> roots = [];
            Hash256 committedRoot;
            await using (PbtTestContext context = new(database, config))
            {
                using (PbtWorldStateScope scope = context.BeginScope(null))
                {
                    Mutate(scope, 1);
                    scope.Commit(1);
                    roots.Add(scope.Bundle.TreeRoot);
                    committedRoot = scope.RootHash;
                }
                BlockHeader parent = Build.A.BlockHeader.WithNumber(1).WithStateRoot(committedRoot).TestObject;
                using (PbtWorldStateScope fork = context.BeginScope(parent))
                {
                    Mutate(fork, 9);
                    fork.Commit(2);
                    roots.Add(fork.Bundle.TreeRoot);
                }
                using (PbtWorldStateScope scope = context.BeginScope(parent))
                {
                    for (uint generation = 2; generation <= 4; generation++)
                    {
                        if (generation == 3)
                        {
                            using IWorldStateScopeProvider.IWorldStateWriteBatch deletion = scope.StartWriteBatch(1);
                            deletion.Set(TestItem.AddressA, null);
                        }
                        else Mutate(scope, generation);
                        scope.UpdateRootHash();
                        Hash256 folded = scope.RootHash;
                        scope.UpdateRootHash();
                        Assert.That(scope.RootHash, Is.EqualTo(folded));
                        scope.Commit(generation);
                        roots.Add(scope.Bundle.TreeRoot);
                        AssertState(scope, generation == 3 ? 0 : generation);
                    }
                    committedRoot = scope.RootHash;
                }
                context.Manager.FlushCache(default);
                using IPbtPersistence.IReader reader = context.Persistence.CreateReader();
                Assert.That(reader.CurrentRoot, Is.EqualTo(roots[^1]));
            }
            await using (PbtTestContext reopened = new(database, config))
            {
                BlockHeader header = Build.A.BlockHeader.WithNumber(4).WithStateRoot(committedRoot).TestObject;
                using PbtWorldStateScope scope = reopened.BeginScope(header);
                Assert.That(scope.Bundle.TreeRoot, Is.EqualTo(roots[^1]));
                AssertState(scope, 4);
            }
            return roots;
        }

        static void Mutate(PbtWorldStateScope scope, uint generation)
        {
            using IWorldStateScopeProvider.IWorldStateWriteBatch batch = scope.StartWriteBatch(1);
            batch.Set(TestItem.AddressA, Build.An.Account.WithBalance(generation).TestObject);
            using IWorldStateScopeProvider.IStorageWriteBatch storage = batch.CreateStorageWriteBatch(TestItem.AddressA, 2);
            storage.Set(7, (UInt256)0xab);
            storage.Set(1000, generation == 2 ? UInt256.Zero : 0xcd);
        }

        static void AssertState(PbtWorldStateScope scope, uint generation)
        {
            IWorldStateScopeProvider.IStorageTree storage = scope.CreateStorageTree(TestItem.AddressA);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(scope.Get(TestItem.AddressA)?.Balance ?? UInt256.Zero, Is.EqualTo((UInt256)generation));
                Assert.That(storage.Get(7), Is.EqualTo(generation == 0 ? UInt256.Zero : (UInt256)0xab));
                Assert.That(storage.Get(1000), Is.EqualTo(generation is 0 or 2 ? UInt256.Zero : (UInt256)0xcd));
            }
        }
    }

    private static IEnumerable<TestCaseData> NodeGroupPrefetchCases()
    {
        yield return new TestCaseData(Bal(Build.An.AccountChanges.WithAddress(TestItem.AddressA).WithBalanceChanges(new BalanceChange(1, 100))), new[] { TestItem.AddressA }, Array.Empty<Address>())
            .SetName("balance writes the account zone");
        yield return new TestCaseData(Bal(Build.An.AccountChanges.WithAddress(TestItem.AddressA).WithStorageChanges(7, new StorageChange(1, 1u))), new[] { TestItem.AddressA }, Array.Empty<Address>())
            .SetName("header slot writes the account zone");
        yield return new TestCaseData(Bal(Build.An.AccountChanges.WithAddress(TestItem.AddressA).WithStorageChanges(1000, new StorageChange(1, 1u)).WithStorageChanges(2000, new StorageChange(1, 2u))), Array.Empty<Address>(), new[] { TestItem.AddressA })
            .SetName("storage-zone slots share one storage path");
        yield return new TestCaseData(Bal(Build.An.AccountChanges.WithAddress(TestItem.AddressA).WithStorageReads(1000)), Array.Empty<Address>(), Array.Empty<Address>())
            .SetName("storage reads write nothing");
        yield return new TestCaseData(Bal(
                Build.An.AccountChanges.WithAddress(TestItem.AddressA).WithNonceChanges(new NonceChange(1, 1)).WithStorageChanges(7, new StorageChange(1, 1u)).WithStorageChanges(1000, new StorageChange(1, 1u)),
                Build.An.AccountChanges.WithAddress(TestItem.AddressB).WithStorageChanges(1000, new StorageChange(1, 1u))),
                new[] { TestItem.AddressA }, new[] { TestItem.AddressA, TestItem.AddressB })
            .SetName("both zones across accounts");

        static ReadOnlyBlockAccessList Bal(params AccountChangesBuilder[] accounts) =>
            Build.A.BlockAccessList.WithAccountChanges([.. accounts.Select(static account => account.TestObject)]).TestObject;
    }

    [TestCaseSource(nameof(NodeGroupPrefetchCases))]
    public void NodeGroupPrefetchPaths_are_the_first_groups_below_the_top_groups(ReadOnlyBlockAccessList bal, Address[] accountZone, Address[] storageZone)
    {
        IEnumerable<PbtStorageNodePath> expected = accountZone.Select(static address => new PbtStorageNodePath(Bytes.FromHexString("00" + PbtStateKey.AddressKeyHash(address).Bytes.ToHexString()[..6]), 32))
            .Concat(storageZone.Select(static address => new PbtStorageNodePath(Bytes.FromHexString("ff" + PbtStateKey.AddressKeyHash(address).Bytes.ToHexString()), 264)));

        Assert.That(PbtWorldStateScope.NodeGroupPrefetchPaths(bal), Is.EquivalentTo(expected));
    }

    [TestCase(-1L, 0, false, TestName = "missing storage group")]
    [TestCase(0L, 0, false)]
    [TestCase(1024L, 0, false)]
    [TestCase(1025L, 1, false)]
    [TestCase(16 * 1024L, 1, false)]
    [TestCase(16 * 1024 + 1L, 2, false)]
    [TestCase(1024 * 1024L, 3, false)]
    [TestCase(1024 * 1024L, 0, true, TestName = "cancelled")]
    public void NodeGroupPrefetch_reads_storage_groups_as_deep_as_the_slot_subtree_size_suggests(long descendantBytes, int levels, bool cancelled)
    {
        ReadOnlyBlockAccessList bal = Build.A.BlockAccessList.WithAccountChanges(
            Build.An.AccountChanges.WithAddress(TestItem.AddressA).WithBalanceChanges(new BalanceChange(1, 100))
                .WithStorageChanges(7, new StorageChange(1, 1u))
                .WithStorageChanges(1000, new StorageChange(1, 1u))
                .WithStorageChanges(1001, new StorageChange(1, 1u))
                .WithStorageChanges(2000, new StorageChange(1, 1u))
                .WithStorageReads(3000).TestObject,
            Build.An.AccountChanges.WithAddress(TestItem.AddressB).WithStorageChanges(1000, new StorageChange(1, 1u)).TestObject).TestObject;
        ValueHash256 addressHash = PbtStateKey.AddressKeyHash(TestItem.AddressA);
        PbtStoragePath storagePath = PbtStateKey.Storage(TestItem.AddressA, addressHash, 1000);
        PbtStorageNodePath storageGroup = new(storagePath.Bytes[..33], 264);
        TrackingMemoryProvider memory = new();
        IPbtPersistence.IReader reader = Substitute.For<IPbtPersistence.IReader>();
        reader.GetNodeGroup(Arg.Any<PbtStorageNodePath>()).Returns(call =>
        {
            PbtStorageNodePath path = call.Arg<PbtStorageNodePath>();
            if (descendantBytes < 0 || path.BitDepth == 264 && !path.Equals(storageGroup)) return null;
            long[] sizes = new long[PbtFourLevelGroupGeometry.BoundarySlots];
            Array.Fill(sizes, descendantBytes);
            byte[] branch = PbtTreeHarness.EncodeBranch([], 0, TestItem.KeccakA.ValueHash256, TestItem.KeccakB.ValueHash256);
            byte[] encoding = PbtNodeGroupEncoder.Encode(path, [new PbtNodeRecord(PbtTestPaths.PathOf(path, 0), branch)], sizes);
            RefCountingMemory payload = memory.Rent(encoding.Length);
            encoding.CopyTo(payload.GetSpan());
            return payload;
        });
        using PbtSnapshotBundle bundle = PbtSnapshotBundleTestExtensions.CreateBundle(new PbtResourcePool(new PbtConfig()), reader);

        PbtWorldStateScope.PrefetchNodeGroups(bundle, bal, new CancellationToken(cancelled));

        HashSet<PbtStorageNodePath> expected = cancelled ? [] : PbtWorldStateScope.NodeGroupPrefetchPaths(bal);
        foreach (UInt256 slot in new UInt256[] { 1000, 1001, 2000 })
        {
            PbtStoragePath key = PbtStateKey.Storage(TestItem.AddressA, addressHash, slot);
            for (int depth = 268; depth <= 264 + 4 * levels; depth += 4)
            {
                byte[] path = key.Bytes[..((depth + 7) / 8)].ToArray();
                if (depth % 8 != 0) path[^1] &= 0xF0;
                expected.Add(new PbtStorageNodePath(path, depth));
            }
        }
        using (Assert.EnterMultipleScope())
        {
            Assert.That(reader.ReceivedCalls().Where(call => call.GetMethodInfo().Name == nameof(IPbtPersistence.IReader.GetNodeGroup))
                .Select(call => (PbtStorageNodePath)call.GetArguments()[0]!), Is.EquivalentTo(expected));
            Assert.That(TrackingMemoryProvider.CountUnreleased(memory.Rented), Is.Zero);
        }
    }

    private static void Write(IWorldStateScopeProvider.IScope scope, byte balance)
    {
        using IWorldStateScopeProvider.IWorldStateWriteBatch batch = scope.StartWriteBatch(1);
        batch.Set(TestItem.AddressA, Build.An.Account.WithBalance(balance).TestObject);
    }

    private static byte[] ReferenceRoot(Dictionary<PbtVariableTreeKey, ValueHash256> leaves)
    {
        EipReferenceTree reference = new();
        foreach ((PbtVariableTreeKey key, ValueHash256 value) in leaves) reference.Insert(key.Bytes, value.Bytes.ToArray());
        return reference.Merkelize();
    }
}
