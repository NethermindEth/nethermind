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
using Nethermind.State.Pbt.ScopeProvider;
using NUnit.Framework;

namespace Nethermind.State.Pbt.Test;

public class PbtScopeProviderTests
{
    private static readonly IReleaseSpec Spec = Prague.Instance;

    [Test]
    public async Task ProcessBlocksThroughWorldState_RootsMatchEipReference_AndHistoricalReadsWork()
    {
        await using PbtTestContext ctx = new();
        WorldState worldState = new(ctx.WorldStateManager.GlobalWorldState, LimboLogs.Instance);

        // > 128 chunks (3968 bytes) so overflow chunks land in the content-addressed code zone
        byte[] bigCode = new byte[5000];
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

        // historical reads through the state reader at both heights
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

    [Test]
    public async Task SelfDestructAndRecreate_ClearsHeaderStem_ButKeepsStaleStorageZoneStems()
    {
        await using PbtTestContext ctx = new();
        PbtScopeProvider provider = ctx.CreateScopeProvider();
        Address address = TestItem.AddressC;
        byte[] slotValue = Bytes.FromHexString("0x0000000000000000000000000000000000000000000000000000000000000099");

        Dictionary<string, byte[]> model = [];
        using IWorldStateScopeProvider.IScope scope = provider.BeginScope(null, new LocalMetrics());

        // block 1: a contract account with a header slot and a storage-zone slot
        using (IWorldStateScopeProvider.IWorldStateWriteBatch batch = scope.StartWriteBatch(1))
        {
            using (IWorldStateScopeProvider.IStorageWriteBatch storageBatch = batch.CreateStorageWriteBatch(address, 2))
            {
                storageBatch.Set(3, slotValue);
                storageBatch.Set(500, slotValue);
            }

            batch.Set(address, new Account(7, 1000));
        }

        scope.UpdateRootHash();
        scope.Commit(1);
        PbtReferenceModel.SetAccount(model, address, 7, 1000);
        PbtReferenceModel.SetSlot(model, address, 3, 0x99);
        PbtReferenceModel.SetSlot(model, address, 500, 0x99);
        Assert.That(scope.RootHash, Is.EqualTo(PbtReferenceModel.Root(model).ToHash256()));

        // block 2: self-destruct clears the whole header stem, but the storage-zone stem of slot
        // 500 intentionally stays in the tree (documented divergence from a fresh merkelization)
        using (IWorldStateScopeProvider.IWorldStateWriteBatch batch = scope.StartWriteBatch(1))
        {
            batch.Set(address, null);
        }

        scope.UpdateRootHash();
        scope.Commit(2);
        PbtReferenceModel.RemoveAccountHeader(model, address);
        Assert.That(scope.RootHash, Is.EqualTo(PbtReferenceModel.Root(model).ToHash256()));

        // flat reads honor the destruct even though the tree keeps the stale stem
        Assert.That(scope.Get(address), Is.Null);
        Assert.That(scope.CreateStorageTree(address).Get(500).IsZero());

        // block 3: re-create the account; only the new writes surface
        using (IWorldStateScopeProvider.IWorldStateWriteBatch batch = scope.StartWriteBatch(1))
        {
            using (IWorldStateScopeProvider.IStorageWriteBatch storageBatch = batch.CreateStorageWriteBatch(address, 1))
            {
                storageBatch.Clear();
                storageBatch.Set(3, slotValue);
            }

            batch.Set(address, new Account(0, 5));
        }

        scope.UpdateRootHash();
        scope.Commit(3);
        PbtReferenceModel.SetAccount(model, address, 0, 5);
        PbtReferenceModel.SetSlot(model, address, 3, 0x99);
        Assert.That(scope.RootHash, Is.EqualTo(PbtReferenceModel.Root(model).ToHash256()));
        // the storage tree returns the stripped (leading-zeros-removed) value, per the EVM contract
        Assert.That(scope.CreateStorageTree(address).Get(3), Is.EqualTo((byte[])[0x99]));
        Assert.That(scope.CreateStorageTree(address).Get(500).AsSpan().IsZero());
    }
}
