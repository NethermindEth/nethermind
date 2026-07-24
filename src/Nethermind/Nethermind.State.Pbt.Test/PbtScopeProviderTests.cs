// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs.Forks;
using Nethermind.Pbt;
using Nethermind.State.Pbt.ScopeProvider;
using NUnit.Framework;

namespace Nethermind.State.Pbt.Test;

/// <param name="layout">
/// The whole component stack — scope, snapshot, compaction, persistence — over each tiling of the
/// trie underneath it, all folding to the same reference roots.
/// </param>
[TestFixture(PbtTrieLayout.ClusteredFourLevelInterleaved)]
[TestFixture(PbtTrieLayout.SixLevelInterleaved)]
[TestFixture(PbtTrieLayout.EightLevelInterleaved)]
public class PbtScopeProviderTests(PbtTrieLayout layout)
{
    private PbtTestContext NewContext() => new(config: new PbtConfig { TrieNodeLayout = layout });

    private static readonly IReleaseSpec Spec = Prague.Instance;

    [Test]
    public async Task ProcessBlocksThroughWorldState_RootsMatchEipReference_AndHistoricalReadsWork()
    {
        await using PbtTestContext ctx = NewContext();
        WorldState worldState = new(ctx.WorldStateManager.GlobalWorldState, LimboLogs.Instance);

        // > 128 + 256 chunks (11904 bytes) so the overflow chunks not only land in the
        // content-addressed code zone but span more than one stem of it
        byte[] bigCode = new byte[15000];
        for (int i = 0; i < bigCode.Length; i += 10)
        {
            bigCode[i] = 0x63; // PUSH4, to exercise the chunk PUSHDATA offsets
        }

        Dictionary<string, byte[]> model = [];

        Hash256 root1;
        using (worldState.BeginScope(IWorldState.PreGenesis))
        {
            worldState.CreateAccount(TestItem.AddressA, 100, 1);
            worldState.CreateAccount(TestItem.AddressB, 42);
            worldState.InsertCode(TestItem.AddressB, ValueKeccak.Compute(bigCode), bigCode, Spec);
            worldState.Set(new StorageCell(TestItem.AddressB, 5), [0xAB]);
            worldState.Set(new StorageCell(TestItem.AddressB, 1000), Bytes.FromHexString("0x1234"));
            worldState.Commit(Spec);
            worldState.CommitTree(1);
            root1 = worldState.StateRoot;
        }

        PbtReferenceModel.SetAccount(model, TestItem.AddressA, 1, 100);
        PbtReferenceModel.SetAccount(model, TestItem.AddressB, 0, 42, bigCode);
        PbtReferenceModel.SetSlot(model, TestItem.AddressB, 5, 0xAB);
        PbtReferenceModel.SetSlot(model, TestItem.AddressB, 1000, 0x1234);
        Assert.That(root1, Is.EqualTo(PbtReferenceModel.Root(model).ToHash256()));

        BlockHeader header1 = Build.A.BlockHeader.WithNumber(1).WithStateRoot(root1).TestObject;
        Hash256 root2;
        using (worldState.BeginScope(header1))
        {
            worldState.AddToBalance(TestItem.AddressA, 5, Spec);
            // a contract receiving ETH is dirty with unchanged code — its BASIC_DATA is rewritten
            // and its code size must be preserved (read back, not recomputed from the code)
            worldState.AddToBalance(TestItem.AddressB, 10, Spec);
            worldState.Set(new StorageCell(TestItem.AddressB, 5), [0]);
            worldState.Set(new StorageCell(TestItem.AddressB, 70), [0x07]);
            worldState.Commit(Spec);
            worldState.CommitTree(2);
            root2 = worldState.StateRoot;
        }

        PbtReferenceModel.SetAccount(model, TestItem.AddressA, 1, 105);
        PbtReferenceModel.SetAccount(model, TestItem.AddressB, 0, 52, bigCode);
        PbtReferenceModel.SetSlot(model, TestItem.AddressB, 5, 0);
        PbtReferenceModel.SetSlot(model, TestItem.AddressB, 70, 0x07);
        Assert.That(root2, Is.EqualTo(PbtReferenceModel.Root(model).ToHash256()));

        BlockHeader header2 = Build.A.BlockHeader.WithNumber(2).WithStateRoot(root2).TestObject;
        Assert.That(ctx.StateReader.TryGetAccount(header1, TestItem.AddressA, out AccountStruct accountAt1), Is.True);
        Assert.That(accountAt1.Balance, Is.EqualTo((UInt256)100));
        Assert.That(ctx.StateReader.TryGetAccount(header2, TestItem.AddressA, out AccountStruct accountAt2), Is.True);
        Assert.That(accountAt2.Balance, Is.EqualTo((UInt256)105));
        Assert.That(ctx.StateReader.TryGetAccount(header2, TestItem.AddressB, out AccountStruct accountBAt2), Is.True);
        Assert.That(accountBAt2.Balance, Is.EqualTo((UInt256)52));
        Assert.That(accountBAt2.CodeHash, Is.EqualTo(ValueKeccak.Compute(bigCode)));
        Assert.That(ctx.StateReader.GetStorage(header1, TestItem.AddressB, 5).ToArray(), Is.EqualTo((byte[])[0xAB]));
        Assert.That(ctx.StateReader.GetStorage(header2, TestItem.AddressB, 5).IsZero());
        Assert.That(ctx.StateReader.GetStorage(header2, TestItem.AddressB, 1000).ToArray(), Is.EqualTo((byte[])[0x12, 0x34]));
    }

    /// <summary>
    /// The world state must not read emptiness off the storage tree's placeholder root: taking it as proof
    /// that the account holds no slot answers every read after the first with zero, and hands the commit a
    /// storage clear that would mark the account self-destructed.
    /// </summary>
    [Test]
    public async Task ReadsThroughWorldState_PastTheFirstSlot_StillSeeStoredValues()
    {
        await using PbtTestContext ctx = NewContext();
        WorldState worldState = new(ctx.WorldStateManager.GlobalWorldState, LimboLogs.Instance);

        byte[] firstValue = Bytes.FromHexString("0xab");
        byte[] secondValue = Bytes.FromHexString("0x1234");
        byte[] writtenAfterRead = Bytes.FromHexString("0x5678");

        Hash256 root1;
        using (worldState.BeginScope(IWorldState.PreGenesis))
        {
            worldState.CreateAccount(TestItem.AddressB, 42);
            worldState.Set(new StorageCell(TestItem.AddressB, 5), firstValue);
            worldState.Set(new StorageCell(TestItem.AddressB, 1000), secondValue);
            worldState.Commit(Spec);
            worldState.CommitTree(1);
            root1 = worldState.StateRoot;
        }

        BlockHeader header1 = Build.A.BlockHeader.WithNumber(1).WithStateRoot(root1).TestObject;
        Hash256 root2;
        using (worldState.BeginScope(header1))
        {
            Assert.That(worldState.Get(new StorageCell(TestItem.AddressB, 5)).ToArray(), Is.EqualTo(firstValue));
            Assert.That(worldState.Get(new StorageCell(TestItem.AddressB, 1000)).ToArray(), Is.EqualTo(secondValue));
            Assert.That(worldState.Get(new StorageCell(TestItem.AddressB, 5)).ToArray(), Is.EqualTo(firstValue));

            worldState.Set(new StorageCell(TestItem.AddressB, 70), writtenAfterRead);
            worldState.Commit(Spec);
            worldState.CommitTree(2);
            root2 = worldState.StateRoot;
        }

        BlockHeader header2 = Build.A.BlockHeader.WithNumber(2).WithStateRoot(root2).TestObject;
        using (worldState.BeginScope(header2))
        {
            Assert.That(worldState.Get(new StorageCell(TestItem.AddressB, 5)).ToArray(), Is.EqualTo(firstValue));
            Assert.That(worldState.Get(new StorageCell(TestItem.AddressB, 1000)).ToArray(), Is.EqualTo(secondValue));
            Assert.That(worldState.Get(new StorageCell(TestItem.AddressB, 70)).ToArray(), Is.EqualTo(writtenAfterRead));
        }
    }

    [Test]
    public async Task StorageWritesWithoutAccountChange_MerkelizeThroughStemPass()
    {
        await using PbtTestContext ctx = NewContext();
        PbtScopeProvider provider = ctx.CreateScopeProvider();
        Address address = TestItem.AddressC;
        byte[] slotValue = Bytes.FromHexString("0x0000000000000000000000000000000000000000000000000000000000000099");

        Dictionary<string, byte[]> model = [];
        using IWorldStateScopeProvider.IScope scope = provider.BeginScope(null, new LocalMetrics());

        // a header-region slot (< 64) and a storage-zone slot (>= 64) with no account entry: the
        // header stem has no dirty account to fold it in, so it must be emitted by the stem pass
        using (IWorldStateScopeProvider.IWorldStateWriteBatch batch = scope.StartWriteBatch(0))
        {
            using IWorldStateScopeProvider.IStorageWriteBatch storageBatch = batch.CreateStorageWriteBatch(address, 2);
            storageBatch.Set(3, slotValue);
            storageBatch.Set(500, slotValue);
        }

        scope.UpdateRootHash();
        scope.Commit(1);
        PbtReferenceModel.SetSlot(model, address, 3, 0x99);
        PbtReferenceModel.SetSlot(model, address, 500, 0x99);
        Assert.That(scope.RootHash, Is.EqualTo(PbtReferenceModel.Root(model).ToHash256()));
    }

    [Test]
    public async Task UnchangedStorageStems_PassThroughReusesTheStoredChainNode_AndRootStaysCorrect()
    {
        await using PbtTestContext ctx = NewContext();
        PbtScopeProvider provider = ctx.CreateScopeProvider();
        Address[] addresses = [TestItem.AddressA, TestItem.AddressB, TestItem.AddressC];

        // Spread storage slots (each 256 apart) land one per stem, so every contract grows a spine of
        // chain nodes down to where its stems part — the runs whose fold the pass-through reuses.
        const int slots = 20;
        static UInt256 Slot(int s) => (UInt256)(64 + s * 256);

        Dictionary<string, byte[]> model = [];

        Hash256 root1;
        using (IWorldStateScopeProvider.IScope scope = provider.BeginScope(null, new LocalMetrics()))
        {
            using (IWorldStateScopeProvider.IWorldStateWriteBatch batch = scope.StartWriteBatch(addresses.Length))
            {
                foreach (Address address in addresses)
                {
                    batch.Set(address, new Account(1, 100));
                    using IWorldStateScopeProvider.IStorageWriteBatch storageBatch = batch.CreateStorageWriteBatch(address, slots);
                    for (int s = 0; s < slots; s++) storageBatch.Set(Slot(s), [(byte)(s + 1)]);
                }
            }

            scope.UpdateRootHash();
            scope.Commit(1);
            root1 = scope.RootHash;
        }

        foreach (Address address in addresses)
        {
            PbtReferenceModel.SetAccount(model, address, 1, 100);
            for (int s = 0; s < slots; s++) PbtReferenceModel.SetSlot(model, address, Slot(s), (UInt256)(s + 1));
        }
        Assert.That(root1, Is.EqualTo(PbtReferenceModel.Root(model).ToHash256()));

        // Change each account but re-write every storage slot to its existing value: each storage stem's
        // target group is left byte-identical, so the descent passes through the stored spine chains and
        // must reproduce their cached node hash without re-folding — the root has to match the reference.
        BlockHeader header1 = Build.A.BlockHeader.WithNumber(1).WithStateRoot(root1).TestObject;
        Hash256 root2;
        using (IWorldStateScopeProvider.IScope scope = provider.BeginScope(header1, new LocalMetrics()))
        {
            using (IWorldStateScopeProvider.IWorldStateWriteBatch batch = scope.StartWriteBatch(addresses.Length))
            {
                foreach (Address address in addresses)
                {
                    batch.Set(address, new Account(2, 150));
                    using IWorldStateScopeProvider.IStorageWriteBatch storageBatch = batch.CreateStorageWriteBatch(address, slots);
                    for (int s = 0; s < slots; s++) storageBatch.Set(Slot(s), [(byte)(s + 1)]);
                }
            }

            scope.UpdateRootHash();
            root2 = scope.RootHash;
        }

        foreach (Address address in addresses) PbtReferenceModel.SetAccount(model, address, 2, 150);
        Assert.That(root2, Is.EqualTo(PbtReferenceModel.Root(model).ToHash256()));
        Assert.That(root2, Is.Not.EqualTo(root1), "the accounts changed, so the root must move");
    }

    [Test]
    public async Task IncrementalUpdateRootHash_FoldsLaterWritesOnTopOfEarlierFold()
    {
        await using PbtTestContext ctx = NewContext();
        PbtScopeProvider provider = ctx.CreateScopeProvider();
        Address address = TestItem.AddressC;

        Dictionary<string, byte[]> model = [];
        using IWorldStateScopeProvider.IScope scope = provider.BeginScope(null, new LocalMetrics());

        // first writes then an explicit fold, which flushes the dirty stems into the overlays
        using (IWorldStateScopeProvider.IWorldStateWriteBatch batch = scope.StartWriteBatch(0))
        {
            using IWorldStateScopeProvider.IStorageWriteBatch storageBatch = batch.CreateStorageWriteBatch(address, 2);
            storageBatch.Set(3, [0x11]);    // header-region slot on the account header stem
            storageBatch.Set(500, [0x11]);  // storage-zone slot on its own stem
        }
        scope.UpdateRootHash();

        // more writes after the fold: slot 4 shares the header stem with slot 3 (so its blob must be
        // read back from the overlay, not the empty bundle) and slot 500 is overwritten
        using (IWorldStateScopeProvider.IWorldStateWriteBatch batch = scope.StartWriteBatch(0))
        {
            using IWorldStateScopeProvider.IStorageWriteBatch storageBatch = batch.CreateStorageWriteBatch(address, 2);
            storageBatch.Set(4, [0x22]);
            storageBatch.Set(500, [0x22]);
        }
        scope.Commit(1);

        PbtReferenceModel.SetSlot(model, address, 3, 0x11);
        PbtReferenceModel.SetSlot(model, address, 4, 0x22);
        PbtReferenceModel.SetSlot(model, address, 500, 0x22);
        Assert.That(scope.RootHash, Is.EqualTo(PbtReferenceModel.Root(model).ToHash256()));
    }
}
