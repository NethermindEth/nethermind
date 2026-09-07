// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Autofac;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.State;
using Nethermind.Specs.Forks;
using Nethermind.State;
using Nethermind.State.Flat;
using NUnit.Framework;

namespace Nethermind.Store.Test;

[TestFixture]
public class FlatSameBlockDestroyTests
{
    [TestCase(true, true)]
    [TestCase(true, false)]
    [TestCase(false, true)]
    [TestCase(false, false)]
    public void Storage_of_a_contract_destroyed_in_its_creation_block_is_gone_before_and_after_persistence(bool recreatedInSameBlock, bool markDestroyed)
    {
        (IWorldState worldState, IStateReader reader, IContainer container) = TestWorldStateFactory.CreateFlatForTestWithStateReader();
        using IContainer _ = container;
        Address contract = TestItem.AddressA;
        StorageCell slot = new(contract, 0);
        BlockHeader header;
        using (worldState.BeginScope(IWorldState.PreGenesis))
        {
            worldState.CreateAccount(contract, 1);
            worldState.Set(slot, [0x01]);
            worldState.Set(new StorageCell(contract, 1), [0x02]);
            worldState.Commit(Frontier.Instance);

            worldState.GetBalance(contract);
            if (markDestroyed) worldState.MarkStorageDestroyed(contract);
            else worldState.ClearStorage(contract);
            worldState.DeleteAccount(contract);
            worldState.Commit(Frontier.Instance);

            if (recreatedInSameBlock)
            {
                worldState.CreateAccount(contract, 0);
                worldState.Commit(Frontier.Instance);
            }

            worldState.CommitTree(0);
            header = Build.A.BlockHeader.WithNumber(0).WithStateRoot(worldState.StateRoot).TestObject;
        }

        byte[] beforeFlush = reader.GetStorage(header, contract, 0).ToArray();
        container.Resolve<IFlatDbManager>().FlushCache(CancellationToken.None);
        byte[] afterFlush = reader.GetStorage(header, contract, 0).ToArray();
        byte[] afterFlushSlot1 = reader.GetStorage(header, contract, 1).ToArray();
        bool accountExists = reader.TryGetAccount(header, contract, out AccountStruct account);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(beforeFlush.IsZero(), Is.True, "read through the snapshot bundle");
            Assert.That(afterFlush.IsZero(), Is.True, "read from the persisted flat column: a contract destroyed in the block it was created in ends the block with no storage");
            Assert.That(afterFlushSlot1.IsZero(), Is.True);
            Assert.That(accountExists, Is.EqualTo(recreatedInSameBlock), "the persisted state is the block's, not an empty one: the re-created account is there and the destroyed one is not");
            if (recreatedInSameBlock) Assert.That(account.Nonce, Is.Zero);
        }
    }
}
