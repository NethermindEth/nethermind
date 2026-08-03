// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Pbt;
using Nethermind.State.Flat.ScopeProvider;
using Nethermind.State.Pbt.ScopeProvider;
using NUnit.Framework;

namespace Nethermind.State.Pbt.Test;

public class PbtWorldStateScopeTests
{
    // StaticPool<T>'s default cap bounds the drain loop.
    private const int MaxPooledStemChanges = 4096;

    /// <summary>
    /// A scope abandoned with pending writes — an exception mid-block, or a branch dropped before its
    /// final fold — never reaches the drain in <c>BuildChanges</c>, so its rented maps must come back
    /// on disposal instead of being lost to the GC, which would silently starve the shared pool.
    /// </summary>
    [Test]
    public async Task Dispose_WithPendingWrites_ReturnsEveryRentedStemChanges()
    {
        DrainStemChangesPool();
        SingleStemChanges first = new();
        SingleStemChanges second = new();
        StaticPool<SingleStemChanges>.Return(first);
        StaticPool<SingleStemChanges>.Return(second);

        await using PbtTestContext ctx = new();
        PbtScopeProvider provider = ctx.CreateScopeProvider();

        using (IWorldStateScopeProvider.IScope scope = provider.BeginScope(null, new LocalMetrics()))
        using (IWorldStateScopeProvider.IWorldStateWriteBatch batch = scope.StartWriteBatch(0))
        {
            // Distinct header and storage stems each rent one map.
            using IWorldStateScopeProvider.IStorageWriteBatch storageBatch = batch.CreateStorageWriteBatch(TestItem.AddressC, 2);
            storageBatch.Set(3, [0x11]);
            storageBatch.Set(500, [0x11]);
        }

        // Disposal alone must return the maps.
        object[] rented = [StaticPool<SingleStemChanges>.Rent(), StaticPool<SingleStemChanges>.Rent()];
        Assert.That(rented, Is.EquivalentTo(new object[] { first, second }));
    }

    /// <summary>Disposing twice must not return a map twice: two owners of one pooled map corrupt an unrelated stem.</summary>
    [Test]
    public async Task Dispose_CalledTwice_DoesNotReturnAStemChangesTwice()
    {
        DrainStemChangesPool();
        SingleStemChanges seeded = new();
        StaticPool<SingleStemChanges>.Return(seeded);

        await using PbtTestContext ctx = new();
        PbtScopeProvider provider = ctx.CreateScopeProvider();

        IWorldStateScopeProvider.IScope scope = provider.BeginScope(null, new LocalMetrics());
        using (IWorldStateScopeProvider.IWorldStateWriteBatch batch = scope.StartWriteBatch(0))
        {
            using IWorldStateScopeProvider.IStorageWriteBatch storageBatch = batch.CreateStorageWriteBatch(TestItem.AddressC, 1);
            storageBatch.Set(500, [0x11]);
        }

        scope.Dispose();
        scope.Dispose();

        Assert.That(StaticPool<SingleStemChanges>.Rent(), Is.SameAs(seeded), "the one rented map must come back exactly once");
        Assert.That(StaticPool<SingleStemChanges>.Rent(), Is.Not.SameAs(seeded), "a second dispose must not re-return it");
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

    [Test]
    public async Task ReplacingCode_RemovesStaleHeaderChunks()
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

        using (IWorldStateScopeProvider.ICodeSetter codeWriter = scope.CodeDb.BeginCodeWrite())
            codeWriter.Set(shortHash.ValueHash256, shortCode);
        using (IWorldStateScopeProvider.IWorldStateWriteBatch batch = scope.StartWriteBatch(1))
            batch.Set(TestItem.AddressA, Build.An.Account.WithCode(shortCode).TestObject);
        scope.Commit(1);

        for (int chunkId = 1; chunkId < 4; chunkId++)
            Assert.That(scope.Bundle.GetLeaf(PbtStateKey.Code(TestItem.AddressA, longHash.ValueHash256, chunkId)), Is.Null);
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

    private static void DrainStemChangesPool()
    {
        SingleStemChanges sentinel = new();
        StaticPool<SingleStemChanges>.Return(sentinel);

        for (int i = 0; i <= MaxPooledStemChanges; i++)
        {
            if (ReferenceEquals(StaticPool<SingleStemChanges>.Rent(), sentinel)) return;
        }

        Assert.Fail("the stem-change pool never returned the sentinel, so it could not be drained");
    }
}
