// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Autofac;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.State.Flat.ScopeProvider;
using Nethermind.State.Pbt.Migration;
using Nethermind.State.Pbt.ScopeProvider;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.State.Pbt.Test;

public class MigrationScopeProviderTests
{
    private static BlockHeader Header(ulong number, ulong timestamp) => Build.A.BlockHeader.WithNumber(number).WithTimestamp(timestamp).TestObject;

    [TestCase(true, null, "flat", TestName = "a read goes to flat when it holds the state")]
    [TestCase(false, null, "pbt", TestName = "a read goes to PBT when flat does not hold the state")]
    [TestCase(true, false, "flat", TestName = "a pre-activation target executes on flat")]
    [TestCase(false, false, "flat", TestName = "a pre-activation target executes on flat even when flat does not hold the base")]
    [TestCase(true, true, "pbt", TestName = "the activation block executes on PBT even though flat holds its parent")]
    public void Read_only_composite_executes_by_the_target_and_reads_by_availability(bool flatHolds, bool? targetBinary, string expected)
    {
        IWorldStateScopeProvider flat = Substitute.For<IWorldStateScopeProvider>();
        IWorldStateScopeProvider pbt = Substitute.For<IWorldStateScopeProvider>();
        using MigrationReadOnlyScopeProvider provider = new(flat, pbt, MigrationTestSpecs.Create());
        BlockHeader baseBlock = Header(1, MigrationTestSpecs.Activation - 1);
        BlockHeader? target = targetBinary is { } binary ? Header(2, binary ? MigrationTestSpecs.Activation : MigrationTestSpecs.Activation - 1) : null;
        flat.HasRoot(baseBlock).Returns(flatHolds);
        LocalMetrics metrics = new();
        IWorldStateScopeProvider selected = expected == "flat" ? flat : pbt;
        IWorldStateScopeProvider other = expected == "flat" ? pbt : flat;

        if (target is null)
        {
            provider.TryBeginScope(baseBlock, metrics, out _);
            selected.Received(1).TryBeginScope(baseBlock, metrics, out Arg.Any<IWorldStateScopeProvider.IScope?>());
            other.DidNotReceiveWithAnyArgs().TryBeginScope(default, default!, out Arg.Any<IWorldStateScopeProvider.IScope?>());
        }
        else
        {
            provider.TryBeginScopeAtTarget(target, metrics, out _);
            selected.Received(1).TryBeginScopeAtTarget(target, metrics, out Arg.Any<IWorldStateScopeProvider.IScope?>());
            other.DidNotReceiveWithAnyArgs().TryBeginScopeAtTarget(default!, default!, out Arg.Any<IWorldStateScopeProvider.IScope?>());
        }
    }

    [Test]
    public async Task Main_provider_runs_flat_before_activation_even_when_pbt_holds_the_base_and_pbt_from_activation()
    {
        using IContainer container = new ContainerBuilder()
            .AddModule(new TestNethermindModule(new FlatDbConfig { Enabled = true }))
            .AddSingleton<ISpecProvider>(MigrationTestSpecs.Create())
            .Build();
        await using PbtTestContext pbt = new();
        MigrationScopeProvider provider = new(container.Resolve<FlatWorldStateManager>(), pbt.WorldStateManager, MigrationTestSpecs.Create(), UnavailableStateHeaderProvider.Instance);
        BlockHeader genesis = Build.A.BlockHeader.WithNumber(0).WithTimestamp(0).TestObject;
        BlockHeader block1 = Build.A.BlockHeader.WithParent(genesis).WithTimestamp(12).TestObject;
        BlockHeader activation = Build.A.BlockHeader.WithParent(block1).WithTimestamp(MigrationTestSpecs.Activation).TestObject;

        // Commit genesis into PBT: before activation main processing still runs on flat alone.
        using (IWorldStateScopeProvider.IScope scope = pbt.WorldStateManager.GlobalWorldState.BeginScope(null, new LocalMetrics()))
        {
            scope.UpdateRootHash();
            ((PbtWorldStateScope)scope).UseAuthoritativeRoot(genesis.StateRoot!);
            scope.Commit(0);
        }
        using (Assert.EnterMultipleScope())
        {
            Assert.That(pbt.Manager.HasStateForBlock(new StateId(genesis)), Is.True);
            Assert.That(provider.Select(null, genesis), Is.TypeOf<FlatScopeProvider>(), "genesis is not written into PBT");
            Assert.That(provider.Select(genesis, block1), Is.TypeOf<FlatScopeProvider>(), "PBT holding the base does not make main processing write to it");
            Assert.That(provider.Select(block1, activation), Is.TypeOf<PbtScopeProvider>(), "the activation block runs on PBT");
            Assert.That(provider.Select(activation, null), Is.TypeOf<PbtScopeProvider>());
        }
    }
}
