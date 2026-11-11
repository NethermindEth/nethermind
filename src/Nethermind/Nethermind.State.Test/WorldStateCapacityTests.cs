// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs.Forks;
using Nethermind.State;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Store.Test;

[TestFixture]
public class WorldStateCapacityTests
{
    [Test]
    public void Accounts_and_storage_grow_restore_and_persist(
        [Values(0, 32, WorldState.DefaultInitialAccountCapacity)] int initialAccountCapacity)
    {
        IWorldStateScopeProvider scopeProvider = TestWorldStateFactory.CreateForTest().ScopeProvider;
        WorldState state = new(scopeProvider, LimboLogs.Instance, initialAccountCapacity);
        Address[] addresses = new Address[128];
        for (int i = 0; i < addresses.Length; i++) addresses[i] = new Address(i.ToString("x40"));

        BlockHeader baseBlock;
        using (state.BeginScope(IWorldState.PreGenesis))
        {
            foreach (Address address in addresses)
            {
                state.CreateAccount(address, 7);
                state.Set(new StorageCell(address, 1), 11);
            }

            Snapshot snapshot = state.TakeSnapshot();
            foreach (Address address in addresses)
            {
                state.AddToBalance(address, 3, Prague.Instance);
                state.Set(new StorageCell(address, 1), 23);
                state.Set(new StorageCell(address, 2), 31);
            }
            state.Restore(snapshot);
            AssertValues();

            state.Commit(Prague.Instance, isGenesis: true);
            state.CommitTree(0);
            baseBlock = Build.A.BlockHeader.WithStateRoot(state.StateRoot).TestObject;
        }

        using (state.BeginScope(baseBlock)) AssertValues();

        void AssertValues()
        {
            using (Assert.EnterMultipleScope())
            {
                foreach (Address address in addresses)
                {
                    state.Get(new StorageCell(address, 1), out UInt256 first);
                    state.Get(new StorageCell(address, 2), out UInt256 second);
                    Assert.That(state.GetBalance(address), Is.EqualTo((UInt256)7));
                    Assert.That(first, Is.EqualTo((UInt256)11));
                    Assert.That(second, Is.EqualTo(UInt256.Zero));
                }
            }
        }
    }

    [Test]
    public void Small_capacity_reduces_construction_and_detached_storage_allocations()
    {
        IWorldStateScopeProvider scopeProvider = Substitute.For<IWorldStateScopeProvider>();
        Measure(WorldState.DefaultInitialAccountCapacity);
        Measure(32);

        (long defaultConstruction, long defaultDetach) = Measure(WorldState.DefaultInitialAccountCapacity);
        (long smallConstruction, long smallDetach) = Measure(32);

        TestContext.Out.WriteLine($"WorldState construction: {defaultConstruction:N0} -> {smallConstruction:N0} bytes; storage detach: {defaultDetach:N0} -> {smallDetach:N0} bytes.");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(defaultConstruction - smallConstruction, Is.GreaterThan(200 * 1024));
            Assert.That(defaultDetach - smallDetach, Is.GreaterThan(90 * 1024));
        }

        (long Construction, long Detach) Measure(int capacity)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            WorldState state = new(scopeProvider, LimboLogs.Instance, capacity);
            long construction = GC.GetAllocatedBytesForCurrentThread() - before;
            before = GC.GetAllocatedBytesForCurrentThread();
            using IWorldStateScopeProvider.IBlockChangeSnapshot snapshot = state._persistentStorageProvider.DetachBlockChanges();
            long detach = GC.GetAllocatedBytesForCurrentThread() - before;
            GC.KeepAlive(state);
            return (construction, detach);
        }
    }
}
