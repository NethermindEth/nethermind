// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Pbt;
using Nethermind.State.Flat.ScopeProvider;
using Nethermind.State.Pbt.ScopeProvider;
using NUnit.Framework;
using NSubstitute;

namespace Nethermind.State.Pbt.Test;

public class PbtWorldStateScopeTests
{
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
        byte[] code = new byte[(PbtKeyDerivation.HeaderCodeChunks + 2) * PbtKeyDerivation.CodeChunkSize];
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
            Assert.That(pending.ContainsKey((PbtStorageFullKey)PbtStateKey.Code(TestItem.AddressA, codeHash.ValueHash256, PbtKeyDerivation.HeaderCodeChunks)), Is.True);
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

    private static void Write(IWorldStateScopeProvider.IScope scope, byte balance)
    {
        using IWorldStateScopeProvider.IWorldStateWriteBatch batch = scope.StartWriteBatch(1);
        batch.Set(TestItem.AddressA, Build.An.Account.WithBalance(balance).TestObject);
    }

    /// <summary>
    /// Empties the process-wide pool so the maps it hands out next are the ones the test seeded.
    /// </summary>
    /// <remarks>
    /// The pool is FIFO, so whatever an earlier test left queued comes out ahead of a freshly
    /// returned sentinel: renting until the sentinel reappears drains exactly the backlog, whatever
    /// its size, without depending on the pool's cap.
    /// </remarks>
    private sealed class RecordingTrieWarmer(bool acceptSlot = true, bool acceptMpmc = true) : ITrieWarmer
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
            return true;
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
