// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.State;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.State.Pbt.Test;

public class PbtStateReaderTests
{
    [Test]
    public async Task HistoricalReadsSpanInMemoryLayersAndPersistence()
    {
        await using PbtTestContext ctx = new();
        Address address = TestItem.AddressA;

        Hash256[] roots = new Hash256[5];
        using IWorldStateScopeProvider.IScope scope = ctx.CreateScopeProvider().BeginScope(null, new LocalMetrics());
        for (ulong number = 1; number <= 2; number++)
        {
            using (IWorldStateScopeProvider.IWorldStateWriteBatch batch = scope.StartWriteBatch(1))
            {
                using (IWorldStateScopeProvider.IStorageWriteBatch storageBatch = batch.CreateStorageWriteBatch(address, 1))
                {
                    storageBatch.Set(1, [(byte)number]);
                }

                batch.Set(address, new Account(number, number * 100));
            }

            scope.UpdateRootHash();
            scope.Commit(number);
            roots[number] = scope.RootHash;
        }

        // persist blocks 1-2 to disk, then keep blocks 3-4 in memory only
        ctx.Manager.FlushCache(default);
        for (ulong number = 3; number <= 4; number++)
        {
            using (IWorldStateScopeProvider.IWorldStateWriteBatch batch = scope.StartWriteBatch(1))
            {
                using (IWorldStateScopeProvider.IStorageWriteBatch storageBatch = batch.CreateStorageWriteBatch(address, 1))
                {
                    storageBatch.Set(1, [(byte)number]);
                }

                batch.Set(address, new Account(number, number * 100));
            }

            scope.UpdateRootHash();
            scope.Commit(number);
            roots[number] = scope.RootHash;
        }

        // persisted floor read
        BlockHeader header2 = Build.A.BlockHeader.WithNumber(2).WithStateRoot(roots[2]).TestObject;
        Assert.That(ctx.StateReader.HasStateForBlock(header2), Is.True);
        Assert.That(ctx.StateReader.TryGetAccount(header2, address, out AccountStruct accountAt2), Is.True);
        Assert.That(accountAt2.Balance, Is.EqualTo((UInt256)200));
        Assert.That(ctx.StateReader.GetStorage(header2, address, 1).ToArray(), Is.EqualTo((byte[])[2]));

        // in-memory layered read above the floor
        BlockHeader header3 = Build.A.BlockHeader.WithNumber(3).WithStateRoot(roots[3]).TestObject;
        BlockHeader header4 = Build.A.BlockHeader.WithNumber(4).WithStateRoot(roots[4]).TestObject;
        Assert.That(ctx.StateReader.TryGetAccount(header3, address, out AccountStruct accountAt3), Is.True);
        Assert.That(accountAt3.Balance, Is.EqualTo((UInt256)300));
        Assert.That(ctx.StateReader.GetStorage(header4, address, 1).ToArray(), Is.EqualTo((byte[])[4]));

        BlockHeader unknown = Build.A.BlockHeader.WithNumber(9).WithStateRoot(TestItem.KeccakA).TestObject;
        Assert.That(ctx.StateReader.HasStateForBlock(unknown), Is.False);
        Assert.That(ctx.StateReader.TryGetAccount(unknown, address, out _), Is.False);
        Assert.That(ctx.StateReader.TryGetAccount(header4, TestItem.AddressB, out _), Is.False);

        // code reads come from the code db, with the empty-code shortcut
        ctx.CodeDb[TestItem.KeccakB.Bytes] = Bytes.FromHexString("0x6001");
        Assert.That(ctx.StateReader.GetCode(TestItem.KeccakB), Is.EqualTo(Bytes.FromHexString("0x6001")));
        Assert.That(ctx.StateReader.GetCode(Keccak.OfAnEmptyString), Is.Empty);
    }
}
