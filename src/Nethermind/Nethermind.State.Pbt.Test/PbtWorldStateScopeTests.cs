// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Autofac;
using Nethermind.Core.Test.Modules;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Pbt;
using Nethermind.State.Flat.ScopeProvider;
using Nethermind.State.Pbt.ScopeProvider;
using Nethermind.State.Pbt.Persistence;
using NUnit.Framework;
using NSubstitute;

namespace Nethermind.State.Pbt.Test;

public class PbtWorldStateScopeTests
{
    [Test]
    public async Task Override_bundles_use_the_production_cache_without_changing_canonical_state([Values] bool delete)
    {
        PbtConfig config = new() { Enabled = true };
        await using IContainer container = new ContainerBuilder()
            .AddModule(new TestNethermindModule(config))
            .AddModule(new PbtModule(config))
            .AddSingleton<IPbtChildHeaderSource>(NullPbtChildHeaderSource.Instance)
            .Build();
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
        cache.Clear();
        using IOverridableWorldScope overrides = manager.CreateOverridableWorldScope();
        using (PbtWorldStateScope scope = (PbtWorldStateScope)overrides.WorldState.BeginScope(parent, new LocalMetrics()))
        {
            PbtNodePath rootPath = new([], 0);
            using RefCountingMemory? group = scope.Bundle.GetNodeGroup(rootPath);
            Assert.That(group, Is.Not.Null);
            using RefCountingMemory? cached = cache.TryGet(canonicalRoot.ValueHash256, rootPath, out RefCountingMemory? payload) ? payload : null;
            Assert.That(cached, Is.Not.Null, "the override bundle must populate the injected singleton cache");
            Assert.That(cached!.Memory.ToArray(), Is.EqualTo(group!.Memory.ToArray()));
            using (IWorldStateScopeProvider.IWorldStateWriteBatch batch = scope.StartWriteBatch(1))
                batch.Set(TestItem.AddressA, delete ? null : Build.An.Account.WithBalance(2).TestObject);
            scope.Commit(2);
            Assert.That(scope.RootHash, Is.Not.EqualTo(canonicalRoot));
        }
        using IWorldStateScopeProvider.IScope canonical = manager.GlobalWorldState.BeginScope(parent, new LocalMetrics());
        using (Assert.EnterMultipleScope())
        {
            Assert.That(canonical.RootHash, Is.EqualTo(canonicalRoot));
            Assert.That(canonical.Get(TestItem.AddressA)!.Balance, Is.EqualTo((UInt256)1));
        }
    }

    [Test]
    public async Task Storage_emptiness_is_unknown_and_slot_reads_work([Values] bool hasStorage)
    {
        await using PbtTestContext ctx = new();
        using IWorldStateScopeProvider.IScope scope = ctx.CreateScopeProvider().BeginScope(null, new LocalMetrics());
        if (hasStorage)
        {
            using IWorldStateScopeProvider.IWorldStateWriteBatch batch = scope.StartWriteBatch(1);
            batch.Set(TestItem.AddressA, Build.An.Account.WithBalance(1).TestObject);
            using IWorldStateScopeProvider.IStorageWriteBatch storageWriter = batch.CreateStorageWriteBatch(TestItem.AddressA, 1);
            storageWriter.Set(1000, Bytes.FromHexString("ab"));
        }

        IWorldStateScopeProvider.IStorageTree storage = scope.CreateStorageTree(TestItem.AddressA);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(storage.IsKnownEmpty, Is.False);
            Assert.That(storage.Get(1000), Is.EqualTo(hasStorage ? Bytes.FromHexString("ab") : StorageTree.ZeroBytes));
            Assert.That(storage.Get(1001), Is.EqualTo(StorageTree.ZeroBytes));
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
        using (IWorldStateScopeProvider.IWorldStateWriteBatch batch = scope.StartWriteBatch(1))
        {
            batch.Set(TestItem.AddressA, Build.An.Account.WithBalance(1).TestObject);
        }

        scope.UpdateRootHash();
        scope.Commit(1);

        Assert.That(scope.Get(TestItem.AddressA)?.Balance, Is.EqualTo(UInt256.One), "the scope reads back what it committed");
        Assert.That(ctx.Repository.Count, Is.Zero, "and the layer never reaches the repository");
    }

    /// <summary>
    /// The block's header already claims a root, so that is what the scope must report and key its
    /// state by; the root the tree folds to is kept beside it, on the sealed layer, for the next fold.
    /// Committing must also carry the resolved header forward, or the block after it in the same
    /// branch would resolve the child of the block just committed — itself.
    /// </summary>
    [Test]
    public async Task Commit_ReportsTheHeaderRoot_AndSealsTheTreeRootBesideIt()
    {
        PbtTestChildHeaders childHeaders = new();
        await using PbtTestContext ctx = new(childHeaders: childHeaders);

        // block 0 has no header to echo, so its own tree root becomes the genesis header's claim
        using IWorldStateScopeProvider.IScope genesisScope = ctx.CreateScopeProvider().BeginScope(null, new LocalMetrics());
        Write(genesisScope, 1);
        genesisScope.Commit(0);
        BlockHeader genesis = Build.A.BlockHeader.WithNumber(0).WithStateRoot(genesisScope.RootHash).TestObject;

        BlockHeader first = childHeaders.Add(genesis, TestItem.KeccakA);
        BlockHeader second = childHeaders.Add(first, TestItem.KeccakB);

        using IWorldStateScopeProvider.IScope scope = ctx.CreateScopeProvider().BeginScope(genesis, new LocalMetrics());
        Write(scope, 2);
        scope.Commit(1);
        Assert.That(scope.RootHash, Is.EqualTo(TestItem.KeccakA), "the first block reports what its header claims");

        Write(scope, 3);
        scope.Commit(2);
        Assert.That(scope.RootHash, Is.EqualTo(TestItem.KeccakB), "and the next block in the branch resolves its own header");

        IPbtDbManager manager = ctx.Manager;
        Assert.That(manager.HasStateForBlock(new StateId(first)), Is.True, "both states are keyed by their header");
        Assert.That(manager.HasStateForBlock(new StateId(second)), Is.True);

        using PbtSnapshotBundle bundle = manager.GatherBundle(new StateId(second), PbtResourcePool.Usage.ReadOnlyProcessingEnv);
        Assert.That(bundle.TreeRoot, Is.Not.EqualTo(TestItem.KeccakB.ValueHash256), "the tree folded to a root of its own");
        Assert.That(bundle.GetAccount(TestItem.AddressA)!.Balance, Is.EqualTo((UInt256)3), "and the state is readable through the header-keyed id");
    }

    [TestCase(7u, TestName = "header slot, on the account's own stem")]
    [TestCase(1000u, TestName = "storage-zone slot, on a stem of its own")]
    public async Task DeletedAccount_RemovesAccountAndStorage(uint slot)
    {
        await using PbtTestContext ctx = new();
        using IWorldStateScopeProvider.IScope scope = ctx.CreateScopeProvider().BeginScope(null, new LocalMetrics());

        using (IWorldStateScopeProvider.IWorldStateWriteBatch batch = scope.StartWriteBatch(1))
        {
            batch.Set(TestItem.AddressA, Build.An.Account.WithBalance(1).WithNonce(2).TestObject);
            using IWorldStateScopeProvider.IStorageWriteBatch storage = batch.CreateStorageWriteBatch(TestItem.AddressA, 1);
            storage.Set(slot, [0xAB]);
        }

        scope.Commit(0);
        using (IWorldStateScopeProvider.IWorldStateWriteBatch batch = scope.StartWriteBatch(1))
        {
            batch.Set(TestItem.AddressA, null);
        }
        scope.Commit(1);

        Assert.That(scope.Get(TestItem.AddressA), Is.Null);
        Assert.That(scope.CreateStorageTree(TestItem.AddressA).Get(slot), Is.EqualTo(StorageTree.ZeroBytes));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task WholeCode_IsRetainedByBundle_AfterCommit(bool writeAccount)
    {
        byte[] code = Bytes.FromHexString("6001");
        Hash256 codeHash = Keccak.Compute(code);
        await using PbtTestContext ctx = new();
        using PbtWorldStateScope scope = (PbtWorldStateScope)ctx.CreateScopeProvider().BeginScope(null, new LocalMetrics());
        scope.Commit(0);

        using (IWorldStateScopeProvider.ICodeSetter codeWriter = scope.CodeDb.BeginCodeWrite())
            codeWriter.Set(codeHash.ValueHash256, code);
        Assert.That(scope.Bundle.GetCode(codeHash.ValueHash256)!.Code.ToArray(), Is.EqualTo(code));

        if (writeAccount)
        {
            using IWorldStateScopeProvider.IWorldStateWriteBatch batch = scope.StartWriteBatch(1);
            batch.Set(TestItem.AddressA, Build.An.Account.WithCode(code).TestObject);
        }
        scope.Commit(0);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(scope.Bundle.GetCode(codeHash.ValueHash256)!.Code.ToArray(), Is.EqualTo(code));
            Assert.That(scope.CodeDb.GetCode(codeHash.ValueHash256), Is.EqualTo(code));
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task ReplacingCode_RemovesStaleHeaderChunks(bool codeAfterAccount)
    {
        byte[] longCode = new byte[100];
        Array.Fill(longCode, (byte)0x01);
        byte[] shortCode = [0x02];
        Hash256 longHash = Keccak.Compute(longCode);
        Hash256 shortHash = Keccak.Compute(shortCode);
        await using PbtTestContext ctx = new();
        using PbtWorldStateScope scope = (PbtWorldStateScope)ctx.CreateScopeProvider().BeginScope(null, new LocalMetrics());

        using (IWorldStateScopeProvider.ICodeSetter codeWriter = scope.CodeDb.BeginCodeWrite())
            codeWriter.Set(longHash.ValueHash256, longCode);
        using (IWorldStateScopeProvider.IWorldStateWriteBatch batch = scope.StartWriteBatch(1))
            batch.Set(TestItem.AddressA, Build.An.Account.WithCode(longCode).TestObject);
        scope.Commit(0);

        if (!codeAfterAccount) WriteShortCode();
        using (IWorldStateScopeProvider.IWorldStateWriteBatch batch = scope.StartWriteBatch(1))
            batch.Set(TestItem.AddressA, Build.An.Account.WithCode(shortCode).TestObject);
        if (codeAfterAccount) WriteShortCode();
        scope.Commit(1);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(scope.Bundle.GetCodeReference(longHash.ValueHash256), Is.Zero);
            Assert.That(scope.Bundle.GetCodeReference(shortHash.ValueHash256), Is.EqualTo(1));
            for (int chunkId = 1; chunkId < 4; chunkId++)
                Assert.That(ReadDerivedLeaf(scope.Bundle, (PbtStorageFullKey)PbtStateKey.Code(TestItem.AddressA, longHash.ValueHash256, chunkId)), Is.Null);
        }

        void WriteShortCode()
        {
            using IWorldStateScopeProvider.ICodeSetter codeWriter = scope.CodeDb.BeginCodeWrite();
            codeWriter.Set(shortHash.ValueHash256, shortCode);
        }
    }

    [TestCase(7u, false)]
    [TestCase(1000u, false)]
    [TestCase(1000u, true)]
    public async Task RootUpdates_PreservePartitionLeavesThroughRepeatedFoldsAndCommit(uint updatedSlot, bool codeAfterAccount)
    {
        byte[] code = new byte[(PbtKeyDerivation.StemSubtreeWidth + 2) * PbtKeyDerivation.CodeChunkSize];
        Array.Fill(code, (byte)0x01);
        Hash256 codeHash = Keccak.Compute(code);
        await using PbtTestContext ctx = new();
        using PbtWorldStateScope scope = (PbtWorldStateScope)ctx.CreateScopeProvider().BeginScope(null, new LocalMetrics());
        if (!codeAfterAccount) WriteCode();
        using (IWorldStateScopeProvider.IWorldStateWriteBatch batch = scope.StartWriteBatch(1))
        {
            batch.Set(TestItem.AddressA, Build.An.Account.WithBalance(1).WithCode(code).TestObject);
            using IWorldStateScopeProvider.IStorageWriteBatch storage = batch.CreateStorageWriteBatch(TestItem.AddressA, 2);
            storage.Set(7, Bytes.FromHexString("ab"));
            storage.Set(1000, Bytes.FromHexString("cd"));
        }

        if (codeAfterAccount) WriteCode();
        Assert.That(scope.CreateStorageTree(TestItem.AddressA).Get(7), Is.EqualTo(Bytes.FromHexString("ab")));
        scope.UpdateRootHash();
        Assert.That(scope.Bundle.PendingMutationCount, Is.Zero);
        Dictionary<PbtStorageFullKey, ValueHash256> pending = new(scope.Bundle.EnumerateLeaves());
        Hash256 initialRoot = scope.RootHash;
        scope.UpdateRootHash();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(scope.RootHash, Is.EqualTo(initialRoot));
            Assert.That(scope.Bundle.EnumeratePendingLeafMutationsForTest(), Is.Empty);
            Assert.That(scope.Bundle.EnumerateLeaves(), Is.EquivalentTo(pending));
            Assert.That(pending.ContainsKey(PbtStateKey.Storage(TestItem.AddressA, 7)), Is.True);
            Assert.That(pending.ContainsKey(PbtStateKey.Storage(TestItem.AddressA, 1000)), Is.True);
            Assert.That(pending.ContainsKey((PbtStorageFullKey)PbtStateKey.Code(TestItem.AddressA, codeHash.ValueHash256, PbtKeyDerivation.StemSubtreeWidth)), Is.True);
        }

        using (IWorldStateScopeProvider.IWorldStateWriteBatch batch = scope.StartWriteBatch(1))
        using (IWorldStateScopeProvider.IStorageWriteBatch storage = batch.CreateStorageWriteBatch(TestItem.AddressA, 1))
            storage.Set(updatedSlot, Bytes.FromHexString("ef"));
        scope.Get(TestItem.AddressB);
        KeyValuePair<PbtStorageFullKey, ValueHash256?>[] secondFold = [.. scope.Bundle.EnumeratePendingLeafMutationsForTest()];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(secondFold.Length, Is.EqualTo(1));
            Assert.That(secondFold[0].Key, Is.EqualTo(PbtStateKey.Storage(TestItem.AddressA, updatedSlot)));
        }
        scope.UpdateRootHash();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(scope.LastFoldMutationCount, Is.EqualTo(1));
            Assert.That(scope.Bundle.PendingMutationCount, Is.Zero);
        }
        PbtStorageFullKey updatedKey = PbtStateKey.Storage(TestItem.AddressA, updatedSlot);
        foreach ((PbtStorageFullKey key, ValueHash256 value) in pending)
            if (!key.Equals(updatedKey)) Assert.That(ReadDerivedLeaf(scope.Bundle, key), Is.EqualTo(value), key.Bytes.ToArray().ToHexString());

        EipReferenceTree reference = new();
        Dictionary<PbtStorageFullKey, ValueHash256> expectedLeaves = new(scope.Bundle.EnumerateLeaves());
        foreach ((PbtStorageFullKey key, ValueHash256 value) in expectedLeaves)
            reference.Insert(key.Bytes, value.Bytes.ToArray());
        Assert.That(scope.RootHash.Bytes.ToArray(), Is.EqualTo(reference.Merkelize()));
        scope.Commit(0);

        IPbtDbManager manager = ctx.Manager;
        using PbtSnapshotBundle reopened = manager.GatherBundle(new StateId(0, scope.RootHash), PbtResourcePool.Usage.ReadOnlyProcessingEnv);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(reopened.EnumerateLeaves(), Is.EquivalentTo(expectedLeaves));
            Assert.That(reopened.TreeRoot.Bytes.ToArray(), Is.EqualTo(reference.Merkelize()));
        }
        void WriteCode()
        {
            using IWorldStateScopeProvider.ICodeSetter codeWriter = scope.CodeDb.BeginCodeWrite();
            codeWriter.Set(codeHash.ValueHash256, code);
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task Storage_clear_recreates_only_later_writes_across_folds(bool deleteAccount)
    {
        await using PbtTestContext ctx = new();
        using PbtWorldStateScope scope = (PbtWorldStateScope)ctx.CreateScopeProvider().BeginScope(null, new LocalMetrics());
        using (IWorldStateScopeProvider.IWorldStateWriteBatch batch = scope.StartWriteBatch(1))
        {
            batch.Set(TestItem.AddressA, Build.An.Account.WithBalance(1).TestObject);
            using IWorldStateScopeProvider.IStorageWriteBatch storage = batch.CreateStorageWriteBatch(TestItem.AddressA, 1);
            storage.Set(1001, Bytes.FromHexString("ab"));
        }
        scope.UpdateRootHash();
        using (IWorldStateScopeProvider.IWorldStateWriteBatch batch = scope.StartWriteBatch(1))
        {
            using IWorldStateScopeProvider.IStorageWriteBatch storage = batch.CreateStorageWriteBatch(TestItem.AddressA, 2);
            storage.Set(1000, Bytes.FromHexString("cd"));
            if (deleteAccount)
            {
                batch.Set(TestItem.AddressA, null);
                batch.Set(TestItem.AddressA, Build.An.Account.WithBalance(2).TestObject);
            }
            else storage.Clear();
            storage.Set(2000, Bytes.FromHexString("ef"));
        }
        AssertStorage();
        scope.UpdateRootHash();
        AssertStorage();
        EipReferenceTree reference = new();
        Dictionary<PbtStorageFullKey, ValueHash256> expected = new(scope.Bundle.EnumerateLeaves());
        foreach ((PbtStorageFullKey key, ValueHash256 value) in expected) reference.Insert(key.Bytes, value.Bytes.ToArray());
        Assert.That(scope.RootHash.Bytes.ToArray(), Is.EqualTo(reference.Merkelize()));
        scope.Commit(0);
        IPbtDbManager manager = ctx.Manager;
        using PbtSnapshotBundle reopened = manager.GatherBundle(new StateId(0, scope.RootHash), PbtResourcePool.Usage.ReadOnlyProcessingEnv);
        Assert.That(reopened.EnumerateLeaves(), Is.EquivalentTo(expected));

        void AssertStorage()
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(scope.CreateStorageTree(TestItem.AddressA).Get(1001), Is.EqualTo(StorageTree.ZeroBytes));
                Assert.That(scope.CreateStorageTree(TestItem.AddressA).Get(1000), Is.EqualTo(StorageTree.ZeroBytes));
                Assert.That(scope.CreateStorageTree(TestItem.AddressA).Get(2000), Is.EqualTo(Bytes.FromHexString("ef")));
                Assert.That(ReadDerivedLeaf(scope.Bundle, PbtStateKey.Storage(TestItem.AddressA, 1001)), Is.Null);
                Assert.That(ReadDerivedLeaf(scope.Bundle, PbtStateKey.Storage(TestItem.AddressA, 1000)), Is.Null);
            }
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task Parallel_storage_clears_preserve_other_accounts_pending_writes(bool foldBeforeClear)
    {
        await using PbtTestContext ctx = new();
        using PbtWorldStateScope scope = (PbtWorldStateScope)ctx.CreateScopeProvider().BeginScope(null, new LocalMetrics());
        using IWorldStateScopeProvider.IWorldStateWriteBatch batch = scope.StartWriteBatch(16);
        for (int index = 0; index < 16; index++)
        {
            Address address = TestItem.Addresses[index];
            batch.Set(address, Build.An.Account.WithBalance(1).TestObject);
            using IWorldStateScopeProvider.IStorageWriteBatch storage = batch.CreateStorageWriteBatch(address, 1);
            storage.Set(1000, Bytes.FromHexString("ab"));
        }
        if (foldBeforeClear) scope.UpdateRootHash();

        Parallel.For(0, 16, index =>
        {
            using IWorldStateScopeProvider.IStorageWriteBatch storage = batch.CreateStorageWriteBatch(TestItem.Addresses[index], 128);
            for (uint slot = 1001; slot < 1129; slot++)
            {
                if (index % 2 == 0 && slot % 16 == 0) storage.Clear();
                storage.Set(slot, Bytes.FromHexString("cd"));
            }
        });
        scope.UpdateRootHash();
        for (int index = 0; index < 16; index++)
        {
            IWorldStateScopeProvider.IStorageTree storage = scope.CreateStorageTree(TestItem.Addresses[index]);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(storage.Get(1000), Is.EqualTo(index % 2 == 0 ? StorageTree.ZeroBytes : Bytes.FromHexString("ab")));
                Assert.That(storage.Get(1001), Is.EqualTo(index % 2 == 0 ? StorageTree.ZeroBytes : Bytes.FromHexString("cd")));
                Assert.That(storage.Get(1128), Is.EqualTo(Bytes.FromHexString("cd")));
            }
        }
        EipReferenceTree reference = new();
        Dictionary<PbtStorageFullKey, ValueHash256> expected = new(scope.Bundle.EnumerateLeaves());
        foreach ((PbtStorageFullKey key, ValueHash256 value) in expected) reference.Insert(key.Bytes, value.Bytes.ToArray());
        Assert.That(scope.RootHash.Bytes.ToArray(), Is.EqualTo(reference.Merkelize()));
        scope.Commit(0);
        IPbtDbManager manager = ctx.Manager;
        using PbtSnapshotBundle reopened = manager.GatherBundle(new StateId(0, scope.RootHash), PbtResourcePool.Usage.ReadOnlyProcessingEnv);
        Assert.That(reopened.EnumerateLeaves(), Is.EquivalentTo(expected));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task Parallel_storage_writes_and_abandoned_scope_do_not_contaminate_reused_builder(bool foldBeforeAbandon)
    {
        await using PbtTestContext ctx = new();
        using (PbtWorldStateScope abandoned = (PbtWorldStateScope)ctx.CreateScopeProvider().BeginScope(null, new LocalMetrics()))
        {
            using IWorldStateScopeProvider.IWorldStateWriteBatch batch = abandoned.StartWriteBatch(0);
            Parallel.For(0, 32, index =>
            {
                using IWorldStateScopeProvider.IStorageWriteBatch storage = batch.CreateStorageWriteBatch(TestItem.AddressA, 1);
                storage.Set((UInt256)(uint)(1000 + index), Bytes.FromHexString("ab"));
            });
            Assert.That(abandoned.Bundle.PendingMutationCount, Is.EqualTo(32));
            if (foldBeforeAbandon) abandoned.UpdateRootHash();
            for (uint index = 0; index < 32; index++)
                Assert.That(abandoned.CreateStorageTree(TestItem.AddressA).Get(1000 + index), Is.EqualTo(Bytes.FromHexString("ab")));
        }
        using PbtWorldStateScope reused = (PbtWorldStateScope)ctx.CreateScopeProvider().BeginScope(null, new LocalMetrics());
        using (Assert.EnterMultipleScope())
        {
            Assert.That(reused.Bundle.PendingMutationCount, Is.Zero);
            Assert.That(reused.Bundle.EnumerateLeaves(), Is.Empty);
            Assert.That(reused.CreateStorageTree(TestItem.AddressA).Get(1000), Is.EqualTo(StorageTree.ZeroBytes));
        }
    }

    [Test]
    public async Task Warm_hints_use_the_expected_queue([Values(-1, 7, 1000)] int slot, [Values] bool singleProducer)
    {
        RecordingTrieWarmer warmer = new(acceptSlot: false);
        await using PbtTestContext ctx = new(trieWarmer: warmer);
        using PbtWorldStateScope scope = (PbtWorldStateScope)ctx.CreateScopeProvider().BeginScope(null, new LocalMetrics());
        if (slot < 0) scope.HintGet(TestItem.AddressA, null);
        else if (singleProducer) scope.CreateStorageTree(TestItem.AddressA).HintSet((UInt256)(uint)slot, null);
        else scope.HintWarmSlot(new ValueAddress(TestItem.AddressA.Bytes), (UInt256)(uint)slot);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(warmer.AddressJobs, Is.EqualTo(slot < 0 ? 1 : 0));
            Assert.That(warmer.SlotJobs, Is.EqualTo(slot >= 0 && singleProducer ? 1 : 0));
            Assert.That(warmer.MpmcSlotJobs, Is.EqualTo(slot >= 0 ? 1 : 0));
            Assert.That(ExecuteHint(warmer, slot), Is.True);
        }
    }

    [Test]
    public async Task Read_only_provider_does_not_queue_warmup_jobs()
    {
        RecordingTrieWarmer warmer = new();
        await using PbtTestContext ctx = new(trieWarmer: warmer);
        using IWorldStateScopeProvider.IScope scope = ctx.CreateScopeProvider(isReadOnly: true).BeginScope(null, new LocalMetrics());
        using IWorldStateScopeProvider.ITrieWarmupSession session = scope.CreateTrieWarmupSession();
        session.HintWarmAccount(new ValueAddress(TestItem.AddressA.Bytes));
        session.HintWarmSlot(new ValueAddress(TestItem.AddressA.Bytes), 7);
        scope.HintGet(TestItem.AddressB, null);
        scope.CreateStorageTree(TestItem.AddressB).HintSet(1000, null);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(session, Is.SameAs(IWorldStateScopeProvider.ITrieWarmupSession.Noop.Instance));
            Assert.That(warmer.AddressJobs, Is.Zero);
            Assert.That(warmer.SlotJobs, Is.Zero);
            Assert.That(warmer.MpmcSlotJobs, Is.Zero);
        }
    }

    [Test]
    public async Task Retired_jobs_are_rejected_without_draining_the_queue([Values] bool disposeScope, [Values] bool acceptJobs)
    {
        RecordingTrieWarmer warmer = new(acceptSlot: acceptJobs, acceptMpmc: acceptJobs, acceptAddress: acceptJobs);
        await using PbtTestContext ctx = new(trieWarmer: warmer);
        using PbtWorldStateScope scope = (PbtWorldStateScope)ctx.CreateScopeProvider().BeginScope(null, new LocalMetrics());
        using IWorldStateScopeProvider.ITrieWarmupSession firstBorrow = scope.CreateTrieWarmupSession();
        IWorldStateScopeProvider.ITrieWarmupSession secondBorrow = scope.CreateTrieWarmupSession();
        secondBorrow.Dispose();
        firstBorrow.HintWarmAccount(new ValueAddress(TestItem.AddressA.Bytes));
        firstBorrow.HintWarmSlot(new ValueAddress(TestItem.AddressA.Bytes), 1000);
        Assert.That(ExecuteHint(warmer, -1), Is.True, "disposing another borrow must not stop the scope-owned session");

        await Task.Run(() => { if (disposeScope) scope.Dispose(); else scope.Commit(0); }).WaitAsync(TimeSpan.FromSeconds(10));
        int queued = warmer.AddressJobs;
        firstBorrow.HintWarmAccount(new ValueAddress(TestItem.AddressB.Bytes));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ExecuteHint(warmer, -1), Is.False);
            Assert.That(ExecuteHint(warmer, 1000), Is.False);
            Assert.That(warmer.AddressJobs, Is.EqualTo(queued));
        }
    }

    [Test]
    public async Task New_sessions_capture_each_committed_generation()
    {
        RecordingTrieWarmer warmer = new();
        await using PbtTestContext ctx = new(trieWarmer: warmer);
        using PbtWorldStateScope scope = (PbtWorldStateScope)ctx.CreateScopeProvider().BeginScope(null, new LocalMetrics());
        for (byte block = 0; block < 4; block++)
        {
            using IWorldStateScopeProvider.ITrieWarmupSession borrow = scope.CreateTrieWarmupSession();
            PbtTrieWarmupSession session = (PbtTrieWarmupSession)borrow;
            Assert.That(session.TreeRoot, Is.EqualTo(scope.Bundle.TreeRoot));
            borrow.HintWarmAccount(new ValueAddress(TestItem.AddressA.Bytes));
            Assert.That(ExecuteHint(warmer, -1), Is.True);
            Assert.That(warmer.AddressJobs, Is.EqualTo(block + 1), "deduplication must rotate with the committed generation");
            Write(scope, (byte)(block + 1));
            scope.Commit(block);
            Assert.That(ExecuteHint(warmer, -1), Is.False);
        }
    }

    [Test]
    public async Task Session_keeps_frozen_local_groups_through_writes_and_folds()
    {
        RecordingTrieWarmer warmer = new();
        await using PbtTestContext ctx = new(trieWarmer: warmer);
        using PbtWorldStateScope scope = (PbtWorldStateScope)ctx.CreateScopeProvider().BeginScope(null, new LocalMetrics());
        Write(scope, 1);
        scope.Commit(0);
        ValueHash256 committedRoot = scope.Bundle.TreeRoot;
        PbtStorageNodePath rootPath = new([], 0);
        byte[] committedGroup = ReadGroup(new PbtSnapshotStore(scope.Bundle), rootPath);
        Write(scope, 2);
        scope.UpdateRootHash();
        using IWorldStateScopeProvider.ITrieWarmupSession borrow = scope.CreateTrieWarmupSession();
        PbtTrieWarmupSession session = (PbtTrieWarmupSession)borrow;
        Assert.That(session.TreeRoot, Is.EqualTo(committedRoot), "capture excludes already-folded uncommitted writes");
        byte[] frozen = ReadGroup((IPbtStore)session, rootPath);
        Assert.That(frozen, Is.EqualTo(committedGroup));
        ValueHash256 frozenRoot = session.TreeRoot;
        for (byte balance = 2; balance < 5; balance++)
        {
            Write(scope, balance);
            Assert.That(ReadGroup((IPbtStore)session, rootPath), Is.EqualTo(frozen));
            scope.UpdateRootHash();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(ReadGroup((IPbtStore)session, rootPath), Is.EqualTo(frozen));
                Assert.That(session.TreeRoot, Is.EqualTo(frozenRoot));
                Assert.That(scope.RootHash.ValueHash256, Is.Not.EqualTo(frozenRoot));
            }
        }
        scope.Dispose();
        Assert.That(ReadGroup((IPbtStore)session, rootPath), Is.EqualTo(frozen), "the outstanding borrow still owns the frozen layer");
    }

    [Test]
    public void Warmed_groups_are_reused_by_the_root_fold([Values(-1, 7, 1000)] int slot)
    {
        (Hash256 Root, int Reads) cold = Fold(false);
        (Hash256 Root, int Reads) warm = Fold(true);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(warm.Root, Is.EqualTo(cold.Root));
            Assert.That(cold.Reads, Is.GreaterThan(0));
            Assert.That(warm.Reads, Is.LessThan(cold.Reads), "the fold must reuse groups read by the queued warm callback");
        }
        TestContext.Out.WriteLine($"slot={slot}: cold fold reads={cold.Reads}, warmed fold reads={warm.Reads}");

        (Hash256 Root, int Reads) Fold(bool warm)
        {
            using PbtTreeHarness tree = new();
            PbtStorageFullKey key = slot < 0
                ? (PbtStorageFullKey)PbtStateKey.Account(TestItem.AddressA, PbtKeyDerivation.BasicDataLeafKey)
                : PbtStateKey.Storage(TestItem.AddressA, (UInt256)(uint)slot);
            byte[] value = new byte[32];
            value[31] = 1;
            tree.ApplyBatch([(key.Bytes.ToArray(), value)]);
            CountingWarmupReader reader = new(tree);
            using PbtTrieNodeCache cache = new(new PbtConfig());
            RecordingTrieWarmer warmer = new();
            using PbtWorldStateScope scope = CreateCountingScope(reader, cache, warmer);
            if (warm)
            {
                if (slot < 0) scope.HintGet(TestItem.AddressA, null);
                else scope.HintWarmSlot(new ValueAddress(TestItem.AddressA.Bytes), (UInt256)(uint)slot);
                Assert.That(ExecuteHint(warmer, slot), Is.True);
                Assert.That(reader.GroupReads, Is.GreaterThan(0));
            }
            int readsBeforeFold = reader.GroupReads;
            using (IWorldStateScopeProvider.IWorldStateWriteBatch batch = scope.StartWriteBatch(1))
            {
                if (slot < 0) batch.Set(TestItem.AddressA, Build.An.Account.WithBalance(2).TestObject);
                else
                {
                    using IWorldStateScopeProvider.IStorageWriteBatch storage = batch.CreateStorageWriteBatch(TestItem.AddressA, 1);
                    storage.Set((UInt256)(uint)slot, Bytes.FromHexString("02"));
                }
            }
            scope.UpdateRootHash();
            return (scope.RootHash, reader.GroupReads - readsBeforeFold);
        }
    }

    [Test]
    public async Task Retirement_waits_only_for_active_operations_and_last_borrow_releases_reader([Values] bool disposeScope)
    {
        using PbtTreeHarness tree = new();
        CountingWarmupReader reader = new(tree);
        using ManualResetEventSlim entered = new();
        using ManualResetEventSlim release = new();
        reader.BeforeRead = () => { entered.Set(); Assert.That(release.Wait(TimeSpan.FromSeconds(10)), Is.True); };
        RecordingTrieWarmer warmer = new();
        using PbtWorldStateScope scope = CreateCountingScope(reader, null, warmer);
        IWorldStateScopeProvider.ITrieWarmupSession borrow = scope.CreateTrieWarmupSession();
        try
        {
            borrow.HintWarmAccount(new ValueAddress(TestItem.AddressA.Bytes));
            Task<bool> operation = Task.Run(() => ExecuteHint(warmer, -1));
            Task? retirement = null;
            try
            {
                Assert.That(entered.Wait(TimeSpan.FromSeconds(10)), Is.True);
                retirement = Task.Run(() => { if (disposeScope) scope.Dispose(); else scope.Commit(0); });
                Assert.That(SpinWait.SpinUntil(() => ((PbtTrieWarmupSession)borrow).IsStopped, TimeSpan.FromSeconds(10)), Is.True);
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(retirement.IsCompleted, Is.False);
                    Assert.That(reader.DisposeCount, Is.Zero);
                    Assert.That(ExecuteHint(warmer, -1), Is.False);
                }
            }
            finally
            {
                release.Set();
                await operation.WaitAsync(TimeSpan.FromSeconds(10));
                if (retirement is not null) await retirement.WaitAsync(TimeSpan.FromSeconds(10));
            }
            scope.Dispose();
            Assert.That(reader.DisposeCount, Is.Zero, "a retained borrow pins the reader after scope retirement");
        }
        finally
        {
            borrow.Dispose();
        }
        Assert.That(reader.DisposeCount, Is.EqualTo(1));
    }

    [Test]
    public async Task Active_warmup_reads_frozen_state_while_fold_and_cache_clear_complete()
    {
        using PbtTreeHarness tree = new();
        CountingWarmupReader reader = new(tree);
        using PbtTrieNodeCache cache = new(new PbtConfig());
        using ManualResetEventSlim entered = new();
        using ManualResetEventSlim release = new();
        int firstRead = 0;
        reader.BeforeRead = () =>
        {
            if (Interlocked.Increment(ref firstRead) != 1) return;
            entered.Set();
            Assert.That(release.Wait(TimeSpan.FromSeconds(10)), Is.True);
        };
        RecordingTrieWarmer warmer = new();
        using PbtWorldStateScope scope = CreateCountingScope(reader, cache, warmer);
        scope.HintWarmAccount(new ValueAddress(TestItem.AddressA.Bytes));
        Task<bool> operation = Task.Run(() => ExecuteHint(warmer, -1));
        try
        {
            Assert.That(entered.Wait(TimeSpan.FromSeconds(10)), Is.True);
            Write(scope, 2);
            scope.UpdateRootHash();
            Hash256 folded = scope.RootHash;
            cache.Clear();
            scope.UpdateRootHash();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(operation.IsCompleted, Is.False);
                Assert.That(scope.RootHash, Is.EqualTo(folded));
                Assert.That(cache.MemorySize, Is.Zero);
            }
        }
        finally
        {
            release.Set();
            Assert.That(await operation.WaitAsync(TimeSpan.FromSeconds(10)), Is.True);
        }
        scope.Commit(1);
        using PbtTreeHarness expected = new();
        List<(byte[] Key, byte[]? Value)> leaves = [];
        foreach ((PbtFullKey key, ValueHash256 value) in PbtFlatState.AccountLeaves(
            PbtKeyDerivation.AddressKeyHash(TestItem.AddressA), Build.An.Account.WithBalance(2).TestObject, null))
            leaves.Add((key.Bytes.ToArray(), value.Bytes.ToArray()));
        expected.ApplyBatch(leaves);
        Assert.That(scope.Bundle.TreeRoot, Is.EqualTo(expected.RootHash));
    }

    [Test]
    public async Task Cache_and_warmup_preserve_actual_roots_across_forks_deletion_and_reopen(
        [Values(0UL, 1UL, 1048576UL)] ulong cacheBudget, [Values] bool warm)
    {
        List<ValueHash256> expected = await Run(0, false);
        List<ValueHash256> actual = await Run(cacheBudget, warm);
        Assert.That(actual, Is.EqualTo(expected));

        static async Task<List<ValueHash256>> Run(ulong budget, bool warming)
        {
            SnapshotableMemColumnsDb<PbtColumns> database = new("pbt-cache-parity");
            PbtConfig config = new()
            {
                AccountTrieNodeCacheSizeBudget = budget,
                CodeTrieNodeCacheSizeBudget = budget,
                StorageTrieNodeCacheSizeBudget = budget,
                CompactSize = 2,
            };
            RecordingTrieWarmer warmer = new();
            List<ValueHash256> roots = [];
            Hash256 committedRoot;
            await using (PbtTestContext context = new(database, config, trieWarmer: warmer))
            {
                using (PbtWorldStateScope scope = (PbtWorldStateScope)context.CreateScopeProvider().BeginScope(null, new LocalMetrics()))
                {
                    Mutate(scope, 1);
                    scope.Commit(1);
                    roots.Add(scope.Bundle.TreeRoot);
                    committedRoot = scope.RootHash;
                }
                BlockHeader parent = Build.A.BlockHeader.WithNumber(1).WithStateRoot(committedRoot).TestObject;
                using (PbtWorldStateScope fork = (PbtWorldStateScope)context.CreateScopeProvider().BeginScope(parent, new LocalMetrics()))
                {
                    Warm(fork);
                    Mutate(fork, 9);
                    fork.Commit(2);
                    roots.Add(fork.Bundle.TreeRoot);
                }
                using (PbtWorldStateScope scope = (PbtWorldStateScope)context.CreateScopeProvider().BeginScope(parent, new LocalMetrics()))
                {
                    for (uint generation = 2; generation <= 4; generation++)
                    {
                        Warm(scope);
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
                using PbtWorldStateScope scope = (PbtWorldStateScope)reopened.CreateScopeProvider().BeginScope(header, new LocalMetrics());
                Assert.That(scope.Bundle.TreeRoot, Is.EqualTo(roots[^1]));
                AssertState(scope, 4);
            }
            return roots;

            void Warm(PbtWorldStateScope scope)
            {
                if (!warming) return;
                scope.HintWarmAccount(new ValueAddress(TestItem.AddressA.Bytes));
                Assert.That(ExecuteHint(warmer, -1), Is.True);
                foreach (uint slot in new uint[] { 7, 1000 })
                {
                    scope.HintWarmSlot(new ValueAddress(TestItem.AddressA.Bytes), slot);
                    Assert.That(ExecuteHint(warmer, (int)slot), Is.True);
                }
            }
        }

        static void Mutate(PbtWorldStateScope scope, uint generation)
        {
            using IWorldStateScopeProvider.IWorldStateWriteBatch batch = scope.StartWriteBatch(1);
            batch.Set(TestItem.AddressA, Build.An.Account.WithBalance(generation).TestObject);
            using IWorldStateScopeProvider.IStorageWriteBatch storage = batch.CreateStorageWriteBatch(TestItem.AddressA, 2);
            storage.Set(7, Bytes.FromHexString("ab"));
            storage.Set(1000, generation == 2 ? [] : Bytes.FromHexString("cd"));
        }

        static void AssertState(PbtWorldStateScope scope, uint generation)
        {
            IWorldStateScopeProvider.IStorageTree storage = scope.CreateStorageTree(TestItem.AddressA);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(scope.Get(TestItem.AddressA)?.Balance ?? UInt256.Zero, Is.EqualTo((UInt256)generation));
                Assert.That(storage.Get(7), Is.EqualTo(generation == 0 ? StorageTree.ZeroBytes : Bytes.FromHexString("ab")));
                Assert.That(storage.Get(1000), Is.EqualTo(generation is 0 or 2 ? StorageTree.ZeroBytes : Bytes.FromHexString("cd")));
            }
        }
    }

    private static bool ExecuteHint(RecordingTrieWarmer warmer, int slot) => slot < 0
        ? warmer.AddressWarmer!.WarmUpStateTrie(TestItem.AddressA, warmer.AddressSequence)
        : warmer.StorageWarmer!.WarmUpStorageTrie((UInt256)(uint)slot, warmer.SlotSequence);

    private static byte[] ReadGroup<TPath>(IPbtStore store, TPath path) where TPath : struct, IPbtNodePath<TPath>
    {
        using RefCountingMemory? payload = store.GetNodeGroup(path);
        Assert.That(payload, Is.Not.Null);
        return payload!.GetSpan().ToArray();
    }

    private static PbtWorldStateScope CreateCountingScope(CountingWarmupReader reader, PbtTrieNodeCache? cache, ITrieWarmer warmer)
    {
        PbtResourcePool pool = new(new PbtConfig());
        PbtReadOnlySnapshotBundle readOnly = new(new PbtSnapshotPooledList(0), reader);
        PbtSnapshotBundle bundle = new(new PbtSnapshotPooledList(0), readOnly, pool, PbtResourcePool.Usage.MainBlockProcessing, cache);
        return new PbtWorldStateScope(reader.CurrentState, null, bundle, Substitute.For<IWorldStateScopeProvider.ICodeDb>(),
            Substitute.For<IPbtCommitTarget>(), NullPbtChildHeaderSource.Instance, pool, PbtResourcePool.Usage.MainBlockProcessing,
            true, warmer);
    }

    private sealed class CountingWarmupReader(PbtTreeHarness tree) : IPbtPersistence.IReader
    {
        private readonly PbtNodeGroupStore _store = PbtNodeGroupStore.FromPhysicalPayloads(tree.PhysicalPayloads);
        public Action? BeforeRead { get; set; }
        public int GroupReads { get; private set; }
        public int DisposeCount { get; private set; }
        public StateId CurrentState => new(0, CurrentRoot.ToHash256());
        public ValueHash256 CurrentRoot { get; } = tree.RootHash;
        public Account? GetAccount(in ValueHash256 addressHash) => null;
        public EvmWord GetSlot(PbtStorageFullKey key) => default;
        public CodeInfo? GetCode(in ValueHash256 codeHash) => null;
        public ulong GetCodeReference(in ValueHash256 codeHash) => 0;
        public IEnumerable<KeyValuePair<ValueHash256, Account>> EnumerateAccounts() => [];
        public IEnumerable<KeyValuePair<PbtStorageFullKey, EvmWord>> EnumerateStorage(PbtStorageFullKey? prefix = null) => [];
        public IEnumerable<PbtStorageNodePath> EnumerateNodeGroupKeys() => _store.EnumerateNodeGroupKeys();
        public RefCountingMemory? GetNodeGroup<TPath>(TPath groupKey) where TPath : struct, IPbtNodePath<TPath>
        {
            BeforeRead?.Invoke();
            GroupReads++;
            return _store.GetNodeGroup(groupKey);
        }
        public void Dispose()
        {
            DisposeCount++;
            _store.Dispose();
        }
    }

    private static void Write(IWorldStateScopeProvider.IScope scope, byte balance)
    {
        using IWorldStateScopeProvider.IWorldStateWriteBatch batch = scope.StartWriteBatch(1);
        batch.Set(TestItem.AddressA, Build.An.Account.WithBalance(balance).TestObject);
    }

    private sealed class RecordingTrieWarmer(bool acceptSlot = true, bool acceptMpmc = true, bool acceptAddress = true) : ITrieWarmer
    {
        public int AddressJobs { get; private set; }
        public int SlotJobs { get; private set; }
        public int MpmcSlotJobs { get; private set; }
        public int AddressSequence { get; private set; }
        public int SlotSequence { get; private set; }
        public ITrieWarmer.IAddressWarmer? AddressWarmer { get; private set; }
        public ITrieWarmer.IStorageWarmer? StorageWarmer { get; private set; }

        public bool PushSlotJob(ITrieWarmer.IStorageWarmer storageTree, in UInt256 index, int sequenceId)
        {
            SlotJobs++;
            StorageWarmer = storageTree;
            SlotSequence = sequenceId;
            return acceptSlot;
        }

        public bool PushSlotJobMpmc(ITrieWarmer.IStorageWarmer storageTree, in UInt256 index, int sequenceId)
        {
            MpmcSlotJobs++;
            StorageWarmer = storageTree;
            SlotSequence = sequenceId;
            return acceptMpmc;
        }

        public bool PushAddressJob(ITrieWarmer.IAddressWarmer scope, Address? path, int sequenceId)
        {
            AddressJobs++;
            AddressWarmer = scope;
            AddressSequence = sequenceId;
            return acceptAddress;
        }

        public void OnEnterScope()
        {
        }

        public void OnExitScope()
        {
        }
    }

    private static ValueHash256? ReadDerivedLeaf(PbtSnapshotBundle bundle, PbtStorageFullKey key)
    {
        foreach ((PbtStorageFullKey leafKey, ValueHash256 value) in bundle.EnumerateLeaves())
            if (leafKey == key) return value;
        return null;
    }
}
