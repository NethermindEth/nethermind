// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Autofac;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Specs.Forks;
using Nethermind.State;
using NUnit.Framework;

namespace Nethermind.Consensus.Test.Processing;

[TestFixture(false)]
[TestFixture(true)]
[Parallelizable(ParallelScope.All)]
public class BlockAccessListStateReconstructorTests(bool useFlatDb)
{
    [Test]
    public void Applies_last_values_and_clears_storage()
    {
        using IContainer container = CreateContainer();
        IWorldState state = container.Resolve<IWorldState>();
        using IDisposable scope = state.BeginScope(IWorldState.PreGenesis);
        state.CreateAccount(TestItem.AddressA, 100, 1);
        state.InsertCode(TestItem.AddressA, new byte[] { 0x60, 0x00 }, Amsterdam.Instance);
        state.Set(new StorageCell(TestItem.AddressA, 1), 7);
        state.Commit(Amsterdam.Instance);

        ReadOnlyBlockAccessList list = new([
            new ReadOnlyAccountChanges(TestItem.AddressA,
                [new ReadOnlySlotChanges(1, [new StorageChange(1, 9), new StorageChange(2, 0)]),
                 new ReadOnlySlotChanges(2, [new StorageChange(1, 3), new StorageChange(2, 4)])],
                [], [new BalanceChange(1, 50), new BalanceChange(2, 25)],
                [new NonceChange(1, 2), new NonceChange(2, 3)],
                [new CodeChange(1, [0x60, 0x01]), new CodeChange(2, [0x60, 0x02])])], 0);

        BlockAccessListStateReconstructor.Apply(state, list, Amsterdam.Instance);
        state.Commit(Amsterdam.Instance);
        state.Get(new StorageCell(TestItem.AddressA, 1), out UInt256 cleared);
        state.Get(new StorageCell(TestItem.AddressA, 2), out UInt256 changed);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(state.GetBalance(TestItem.AddressA), Is.EqualTo((UInt256)25));
            Assert.That(state.GetNonce(TestItem.AddressA), Is.EqualTo(3UL));
            Assert.That(state.GetCode(TestItem.AddressA).ToArray(), Is.EqualTo(new byte[] { 0x60, 0x02 }));
            Assert.That(cleared, Is.EqualTo(UInt256.Zero));
            Assert.That(changed, Is.EqualTo((UInt256)4));
        }
    }

    [Test]
    public void Read_only_accounts_do_not_create_or_prune_accounts()
    {
        using IContainer container = CreateContainer();
        IWorldState state = container.Resolve<IWorldState>();
        using IDisposable scope = state.BeginScope(IWorldState.PreGenesis);
        state.CreateAccount(TestItem.AddressA, 100);
        state.Commit(Amsterdam.Instance);
        state.RecalculateStateRoot();
        Core.Crypto.Hash256 before = state.StateRoot;
        ReadOnlyBlockAccessList list = new([
            new ReadOnlyAccountChanges(TestItem.AddressA, [], [1], [], [], []),
            new ReadOnlyAccountChanges(TestItem.AddressB)], 0);

        BlockAccessListStateReconstructor.Apply(state, list, Amsterdam.Instance);
        state.Commit(Amsterdam.Instance);
        state.RecalculateStateRoot();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(state.StateRoot, Is.EqualTo(before));
            Assert.That(state.AccountExists(TestItem.AddressB), Is.False);
        }
    }

    [Test]
    public void Creates_accounts_and_prunes_explicitly_emptied_accounts()
    {
        using IContainer container = CreateContainer();
        IWorldState state = container.Resolve<IWorldState>();
        using IDisposable scope = state.BeginScope(IWorldState.PreGenesis);
        state.CreateAccount(TestItem.AddressA, 100, 1);
        state.InsertCode(TestItem.AddressA, new byte[] { 0x60, 0x00 }, Amsterdam.Instance);
        state.Commit(Amsterdam.Instance);
        ReadOnlyBlockAccessList list = new([
            new ReadOnlyAccountChanges(TestItem.AddressA, [], [], [new BalanceChange(1, 0)],
                [new NonceChange(1, 0)], [new CodeChange(1, [])]),
            new ReadOnlyAccountChanges(TestItem.AddressB, [], [], [new BalanceChange(1, 20)], [], [])], 0);

        BlockAccessListStateReconstructor.Apply(state, list, Amsterdam.Instance);
        state.Commit(Amsterdam.Instance);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(state.AccountExists(TestItem.AddressA), Is.False);
            Assert.That(state.GetBalance(TestItem.AddressB), Is.EqualTo((UInt256)20));
        }
    }
    private IContainer CreateContainer() => new ContainerBuilder()
        .AddModule(new TestNethermindModule(new FlatDbConfig { Enabled = useFlatDb }))
        .Map<IWorldStateScopeProvider, IWorldStateManager>(manager => manager.GlobalWorldState)
        .Build();

}
