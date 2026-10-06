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
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs.Forks;
using Nethermind.Pbt;
using Nethermind.State.Pbt.ScopeProvider;
using NUnit.Framework;

namespace Nethermind.State.Pbt.Test;

[TestFixture]
public class PbtScopeProviderTests
{
    private static PbtTestContext NewContext() => new();

    private static readonly IReleaseSpec Spec = Prague.Instance;

    [Test]
    public async Task Delegation_through_world_state_has_canonical_root_after_reopening()
    {
        await using PbtTestContext ctx = NewContext();
        WorldState worldState = new(ctx.WorldStateManager.GlobalWorldState, LimboLogs.Instance);
        BlockHeader? header = IWorldState.PreGenesis;
        Address[] targets = [TestItem.AddressC, TestItem.AddressD, Address.Zero];
        byte[] ordinaryCode = Bytes.FromHexString("6001");
        byte[] zeroCode = Bytes.FromHexString("00");
        for (int index = 0; index < targets.Length; index++)
        {
            byte[] expectedCode = targets[index] == Address.Zero ? [] : [.. Bytes.FromHexString("ef0100"), .. targets[index].Bytes];
            using (worldState.BeginScope(header))
            {
                if (index == 0)
                {
                    worldState.CreateAccount(TestItem.AddressA, 0, 1);
                    worldState.CreateAccount(TestItem.AddressB, 0, 1);
                    worldState.CreateAccount(TestItem.AddressE, 0, 1);
                    worldState.InsertCode(TestItem.AddressB, ValueKeccak.Compute(ordinaryCode), ordinaryCode, Spec);
                    worldState.InsertCode(TestItem.AddressE, ValueKeccak.Compute(zeroCode), zeroCode, Spec);
                }
                CodeInfoRepository.SetDelegation(worldState, targets[index], TestItem.AddressA, Spec, out _, out _);
                worldState.Commit(Spec);
                worldState.CommitTree((ulong)index + 1);
                Dictionary<string, byte[]> model = [];
                PbtReferenceModel.SetAccount(model, TestItem.AddressA, 1, 0, expectedCode);
                PbtReferenceModel.SetAccount(model, TestItem.AddressB, 1, 0, ordinaryCode);
                PbtReferenceModel.SetAccount(model, TestItem.AddressE, 1, 0, zeroCode);
                Assert.That(worldState.StateRoot, Is.EqualTo(PbtReferenceModel.Root(model).ToHash256()));
                header = Build.A.BlockHeader.WithNumber(index + 1).WithStateRoot(worldState.StateRoot).TestObject;
            }
            using (worldState.BeginScope(header))
            {
                Assert.That(worldState.GetCode(TestItem.AddressA).ToArray(), Is.EqualTo(expectedCode));
            }
        }
    }

    [Test]
    public async Task ProcessBlocksThroughWorldState_RootsMatchEipReference_AndHistoricalReadsWork()
    {
        await using PbtTestContext ctx = NewContext();
        Hash256[] roots = PbtTestContext.RunReferenceBlocks(ctx.WorldStateManager.GlobalWorldState);
        byte[] code = PbtTestContext.ReferenceCode;

        Dictionary<string, byte[]> model = [];
        PbtReferenceModel.SetAccount(model, TestItem.AddressA, 1, 100);
        PbtReferenceModel.SetAccount(model, TestItem.AddressB, 0, 42, code);
        PbtReferenceModel.SetSlot(model, TestItem.AddressB, 5, 0xAB);
        PbtReferenceModel.SetSlot(model, TestItem.AddressB, 1000, 0x1234);
        Assert.That(roots[0], Is.EqualTo(PbtReferenceModel.Root(model).ToHash256()));

        PbtReferenceModel.SetAccount(model, TestItem.AddressA, 1, 105);
        PbtReferenceModel.SetAccount(model, TestItem.AddressB, 0, 52, code);
        PbtReferenceModel.SetSlot(model, TestItem.AddressB, 5, 0);
        PbtReferenceModel.SetSlot(model, TestItem.AddressB, 70, 0x07);
        Assert.That(roots[1], Is.EqualTo(PbtReferenceModel.Root(model).ToHash256()));

        PbtReferenceModel.SetAccount(model, TestItem.AddressA, 2, 105);
        Assert.That(roots[2], Is.EqualTo(PbtReferenceModel.Root(model).ToHash256()));

        BlockHeader header1 = Build.A.BlockHeader.WithNumber(1).WithStateRoot(roots[0]).TestObject;
        BlockHeader header2 = Build.A.BlockHeader.WithNumber(2).WithStateRoot(roots[1]).TestObject;
        Assert.That(ctx.StateReader.TryGetAccount(header1, TestItem.AddressA, out AccountStruct accountAt1), Is.True);
        Assert.That(accountAt1.Balance, Is.EqualTo((UInt256)100));
        Assert.That(ctx.StateReader.TryGetAccount(header2, TestItem.AddressA, out AccountStruct accountAt2), Is.True);
        Assert.That(accountAt2.Balance, Is.EqualTo((UInt256)105));
        Assert.That(ctx.StateReader.TryGetAccount(header2, TestItem.AddressB, out AccountStruct accountBAt2), Is.True);
        Assert.That(accountBAt2.Balance, Is.EqualTo((UInt256)52));
        Assert.That(accountBAt2.CodeHash, Is.EqualTo(ValueKeccak.Compute(code)));
        Assert.That(ctx.StateReader.GetStorage(header1, TestItem.AddressB, 5), Is.EqualTo((UInt256)0xAB));
        Assert.That(ctx.StateReader.GetStorage(header2, TestItem.AddressB, 5).IsZero);
        Assert.That(ctx.StateReader.GetStorage(header2, TestItem.AddressB, 1000), Is.EqualTo((UInt256)0x1234));
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
                    for (int s = 0; s < slots; s++) storageBatch.Set(Slot(s), (UInt256)(s + 1));
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
                    for (int s = 0; s < slots; s++) storageBatch.Set(Slot(s), (UInt256)(s + 1));
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

        // first writes then an explicit fold, which flushes the dirty stems into the overlays; with no
        // account entry the header stem has no dirty account to fold it in, so the stem pass must emit it
        using (IWorldStateScopeProvider.IWorldStateWriteBatch batch = scope.StartWriteBatch(0))
        {
            using IWorldStateScopeProvider.IStorageWriteBatch storageBatch = batch.CreateStorageWriteBatch(address, 2);
            storageBatch.Set(3, (UInt256)0x11);    // header-region slot on the account header stem
            storageBatch.Set(500, (UInt256)0x11);  // storage-zone slot on its own stem
        }
        scope.UpdateRootHash();
        PbtReferenceModel.SetSlot(model, address, 3, 0x11);
        PbtReferenceModel.SetSlot(model, address, 500, 0x11);
        Assert.That(scope.RootHash, Is.EqualTo(PbtReferenceModel.Root(model).ToHash256()));

        // more writes after the fold: slot 4 shares the header stem with slot 3 (so its blob must be
        // read back from the overlay, not the empty bundle) and slot 500 is overwritten
        using (IWorldStateScopeProvider.IWorldStateWriteBatch batch = scope.StartWriteBatch(0))
        {
            using IWorldStateScopeProvider.IStorageWriteBatch storageBatch = batch.CreateStorageWriteBatch(address, 2);
            storageBatch.Set(4, (UInt256)0x22);
            storageBatch.Set(500, (UInt256)0x22);
        }
        scope.Commit(1);

        PbtReferenceModel.SetSlot(model, address, 4, 0x22);
        PbtReferenceModel.SetSlot(model, address, 500, 0x22);
        Assert.That(scope.RootHash, Is.EqualTo(PbtReferenceModel.Root(model).ToHash256()));
    }
}
