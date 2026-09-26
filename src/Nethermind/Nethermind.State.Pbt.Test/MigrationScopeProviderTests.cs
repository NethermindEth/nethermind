// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Logging;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using Nethermind.State.Flat;
using Nethermind.State.Flat.ScopeProvider;
using Nethermind.State.Pbt.Migration;
using Nethermind.State.Pbt.Mirror;
using Nethermind.State.Pbt.ScopeProvider;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.State.Pbt.Test;

public class MigrationScopeProviderTests
{
    private const ulong Activation = 48;

    private static ISpecProvider Specs() => new CustomSpecProvider(
        ((ForkActivation)0, Prague.Instance),
        (ForkActivation.TimestampOnly(Activation), new OverridableReleaseSpec(Prague.Instance) { IsEip8347Enabled = true }));

    private static BlockHeader Header(ulong number, ulong timestamp) => Build.A.BlockHeader.WithNumber(number).WithTimestamp(timestamp).TestObject;

    [TestCase(true, null, "flat", TestName = "a read goes to flat when it holds the state")]
    [TestCase(false, null, "pbt", TestName = "a read goes to PBT when flat does not hold the state")]
    [TestCase(true, false, "flat", TestName = "a pre-activation target executes on flat")]
    [TestCase(true, true, "pbt", TestName = "the activation block executes on PBT even though flat holds its parent")]
    public void Read_only_composite_executes_by_the_target_and_reads_by_availability(bool flatHolds, bool? targetBinary, string expected)
    {
        IWorldStateScopeProvider flat = Substitute.For<IWorldStateScopeProvider>();
        IWorldStateScopeProvider pbt = Substitute.For<IWorldStateScopeProvider>();
        using MigrationReadOnlyScopeProvider provider = new(flat, pbt, Specs());
        BlockHeader baseBlock = Header(1, Activation - 1);
        BlockHeader? target = targetBinary is { } binary ? Header(2, binary ? Activation : Activation - 1) : null;
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
    public void Genesis_scope_selects_by_the_genesis_spec()
    {
        IWorldStateScopeProvider flat = Substitute.For<IWorldStateScopeProvider>();
        IWorldStateScopeProvider pbt = Substitute.For<IWorldStateScopeProvider>();
        using MigrationReadOnlyScopeProvider provider = new(flat, pbt, Specs());
        provider.TryBeginScopeAtTarget(Header(0, 0), new LocalMetrics(), out _);
        flat.Received(1).TryBeginScopeAtTarget(Arg.Is<BlockHeader>(header => header.Number == 0), Arg.Any<LocalMetrics>(), out Arg.Any<IWorldStateScopeProvider.IScope?>());
        pbt.DidNotReceiveWithAnyArgs().TryBeginScopeAtTarget(default!, default!, out Arg.Any<IWorldStateScopeProvider.IScope?>());
    }

    [Test]
    public async Task Main_provider_mirrors_in_lockstep_and_runs_flat_alone_below_a_pbt_pointer_that_got_ahead()
    {
        using IContainer container = new ContainerBuilder()
            .AddModule(new TestNethermindModule(new FlatDbConfig { Enabled = true }))
            .AddSingleton<ISpecProvider>(Specs())
            .Build();
        await using PbtTestContext pbt = new();
        FlatWorldStateManager flat = container.Resolve<FlatWorldStateManager>();
        MigrationBackendSelector selector = new(Specs(), pbt.Manager, pbt.Coordinator);
        MigrationScopeProvider provider = new(flat, pbt.WorldStateManager, pbt.Manager, pbt.ResourcePool, selector, pbt.Config, UnavailableStateHeaderProvider.Instance, LimboLogs.Instance);
        BlockHeader genesis = Build.A.BlockHeader.WithNumber(0).WithTimestamp(0).TestObject;
        BlockHeader block1 = Build.A.BlockHeader.WithParent(genesis).WithTimestamp(12).TestObject;
        BlockHeader activation = Build.A.BlockHeader.WithParent(block1).WithTimestamp(Activation).TestObject;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(provider.Select(null, genesis), Is.TypeOf<PbtMirrorScopeProvider>(), "both backends start empty, so genesis is mirrored");
            Assert.That(provider.Select(block1, activation), Is.TypeOf<PbtScopeProvider>(), "the activation block runs on PBT");
            Assert.That(provider.Select(activation, null), Is.TypeOf<PbtScopeProvider>());
        }

        // Commit genesis into PBT only and persist it: PBT is now ahead of a flat that holds nothing.
        using (IWorldStateScopeProvider.IScope scope = pbt.WorldStateManager.GlobalWorldState.BeginScope(null, new LocalMetrics()))
        {
            scope.UpdateRootHash();
            ((PbtWorldStateScope)scope).UseAuthoritativeRoot(genesis.StateRoot!);
            scope.Commit(0);
        }
        pbt.Manager.FlushCache(CancellationToken.None);
        BlockHeader behind = Build.A.BlockHeader.WithNumber(0).WithTimestamp(0).WithStateRoot(TestItem.KeccakA).TestObject;
        BlockHeader parentOfBehind = Build.A.BlockHeader.WithParent(behind).WithTimestamp(12).TestObject;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(selector.PbtHas(genesis), Is.True);
            Assert.That(provider.Select(genesis, block1), Is.TypeOf<PbtMirrorScopeProvider>(), "PBT holds the base: lockstep");
            Assert.That(selector.PbtAhead(block1), Is.False);
            Assert.That(provider.Select(block1, parentOfBehind), Is.TypeOf<PbtMirrorScopeProvider>(), "PBT behind the base: wait for the follower");
        }
        // Persist a second PBT state so the pointer is above block 0.
        using (IWorldStateScopeProvider.IScope scope = pbt.WorldStateManager.GlobalWorldState.BeginScope(genesis, new LocalMetrics()))
        {
            scope.UpdateRootHash();
            ((PbtWorldStateScope)scope).UseAuthoritativeRoot(block1.StateRoot!);
            scope.Commit(1);
        }
        pbt.Manager.FlushCache(CancellationToken.None);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(selector.PbtAhead(behind), Is.True);
            Assert.That(provider.Select(behind, parentOfBehind), Is.TypeOf<FlatScopeProvider>(), "PBT persisted past the base: flat alone");
            Assert.That(provider.Select(block1, activation), Is.TypeOf<PbtScopeProvider>());
        }
    }
}
