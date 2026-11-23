// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Autofac;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs.Forks;
using Nethermind.State.Flat.NodeBuffer;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.PersistedSnapshots;
using Nethermind.State.Flat.Test.NodeBuffer;
using Nethermind.Trie;
using Nethermind.Trie.NodeBuffer;
using NSubstitute;
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

    [Test]
    public void Disposing_the_bundle_releases_its_private_buffer()
    {
        // The private buffer pins the global head when created; disposing it releases that pin.
        List<INodeRecordBufferPin> pins = [];
        INodeRecordBuffer global = Substitute.For<INodeRecordBuffer>();
        global.PinHead().Returns(_ =>
        {
            INodeRecordBufferPin pin = Substitute.For<INodeRecordBufferPin>();
            pins.Add(pin);
            return pin;
        });

        ResourcePool pool = new(new FlatDbConfig { CompactSize = 2 });
        ReadOnlySnapshotBundle readOnly = new(
            new SnapshotPooledList(0),
            Substitute.For<SubstitutablePersistenceReader>(),
            recordDetailedMetrics: false,
            PersistedSnapshotStack.Empty(), bufferPin: null);
        SnapshotBundle bundle = new(readOnly, global, pool, ResourcePool.Usage.ReadOnlyProcessingEnv, privateNodeRecords: true);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(bundle.OwnsNodeRecordBuffer, Is.True);
            Assert.That(pins, Has.Count.EqualTo(1));
        }

        byte[] rlp = NodeRecordBufferTests.Leaf(0, 100);
        INodeRecordBuffer buffer = bundle.NodeRecordBuffer!;
        ulong pointer = buffer.AppendRecordFromRlp(null, TreePath.Empty, ValueKeccak.Compute(rlp), rlp, []);
        Assert.That(buffer.TryLocate(pointer, out NodeRecord record), Is.True);
        pins[0].DidNotReceive().Dispose();

        bundle.Dispose();
        bundle.Dispose();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(() => record.Rlp.ToArray(), Throws.InstanceOf<ObjectDisposedException>(), "the private records are freed");
            pins[0].Received(1).Dispose();
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
