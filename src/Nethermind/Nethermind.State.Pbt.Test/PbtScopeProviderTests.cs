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
    public async Task StorageWritesWithoutAccountChange_MerkelizeThroughStemPass()
    {
        await using PbtTestContext ctx = new();
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
    public async Task IncrementalUpdateRootHash_FoldsLaterWritesOnTopOfEarlierFold()
    {
        await using PbtTestContext ctx = new();
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
