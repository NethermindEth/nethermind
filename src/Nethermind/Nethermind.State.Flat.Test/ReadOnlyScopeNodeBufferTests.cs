// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs.Forks;
using Nethermind.State.Flat.NodeBuffer;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

[Parallelizable(ParallelScope.All)]
public class ReadOnlyScopeNodeBufferTests
{
    private const int Accounts = 64;

    [Test]
    public void Block_building_keeps_its_trie_nodes_out_of_the_global_buffer()
    {
        // 1. Block processing commits a genesis with many accounts and slots.
        // 2. A resettable world state, as block producers use, builds two blocks on top of it and is disposed.
        // 3. The global buffer must not grow from it, while block processing committing the same blocks still writes
        //    to it and reaches the same root, so the built blocks were complete.
        (IWorldState state, _, IContainer container) = TestWorldStateFactory.CreateFlatForTestWithStateReader();
        using (container)
        {
            INodeRecordBuffer global = container.Resolve<INodeRecordBuffer>();
            IWorldStateManager manager = container.Resolve<IWorldStateManager>();

            BlockHeader genesis;
            using (state.BeginScope(IWorldState.PreGenesis))
            {
                for (int i = 0; i < Accounts; i++)
                {
                    state.CreateAccount(TestItem.Addresses[i], (UInt256)1000);
                    state.Set(new StorageCell(TestItem.Addresses[i], (UInt256)1), (UInt256)1);
                }

                state.Commit(Frontier.Instance, isGenesis: true);
                state.CommitTree(0);
                genesis = Build.A.BlockHeader.WithNumber(0).WithStateRoot(state.StateRoot).TestObject;
            }

            ulong beforeBuilding = WriteBoundary(global);
            WorldState building = new(manager.CreateResettableWorldState(), LimboLogs.Instance);
            Hash256 builtRoot;
            UInt256 builtBalance;
            using (building.BeginScope(genesis))
            {
                ApplyBlock(building, 1);
                ApplyBlock(building, 2);
                builtRoot = building.StateRoot;
                builtBalance = building.GetBalance(TestItem.Addresses[0]);
            }

            ulong afterBuilding = WriteBoundary(global);

            Hash256 processedRoot;
            using (state.BeginScope(genesis))
            {
                ApplyBlock(state, 1);
                ApplyBlock(state, 2);
                processedRoot = state.StateRoot;
            }

            using (Assert.EnterMultipleScope())
            {
                Assert.That(afterBuilding, Is.EqualTo(beforeBuilding), "block building must not write to the global buffer");
                Assert.That(WriteBoundary(global), Is.GreaterThan(afterBuilding), "block processing records its trie nodes in the global buffer");
                Assert.That(builtRoot, Is.EqualTo(processedRoot));
                Assert.That(builtRoot, Is.Not.EqualTo(genesis.StateRoot));
                Assert.That(builtBalance, Is.EqualTo((UInt256)1002));
            }
        }
    }

    private static void ApplyBlock(IWorldState state, ulong number)
    {
        for (int i = 0; i < Accounts; i++)
        {
            state.AddToBalance(TestItem.Addresses[i], UInt256.One, Frontier.Instance);
            state.Set(new StorageCell(TestItem.Addresses[i], (UInt256)(number + 1)), (UInt256)number);
        }

        state.Commit(Frontier.Instance);
        state.CommitTree(number);
    }

    private static ulong WriteBoundary(INodeRecordBuffer buffer)
    {
        using INodeRecordBufferPin pin = buffer.PinHead();
        return pin.WriteBoundary;
    }
}
