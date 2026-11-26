// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using Autofac;
using Nethermind.Consensus.Stateless;
using Nethermind.Core;
using Nethermind.Core.Container;
using Nethermind.Core.Test.Modules;
using Nethermind.Evm.State;
using Nethermind.Logging;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using Nethermind.State;
using NUnit.Framework;

namespace Nethermind.Consensus.Test;

public class WitnessGeneratingBlockProcessingEnvFactoryTests
{
    // proof_call runs its call through this env; the processor starts its own EIP-7906 ASSERT diff
    // recorder for a frame transaction, so the env's world state carries none. Checked on a chain that
    // schedules the fork, where a scope-level recorder used to be wired in.
    [Test]
    public void CreateScope_CarriesNoDiffRecorder()
    {
        using IContainer container = new ContainerBuilder()
            .AddModule(new TestNethermindModule(new OverridableReleaseSpec(Cancun.Instance) { IsEip7906Enabled = true, IsEip7928Enabled = true }))
            .Build();

        WorldStateProbeModule probe = new();
        using WitnessGeneratingBlockProcessingEnvFactory factory = new(
            container.Resolve<ILifetimeScope>(),
            container.Resolve<IWorldStateManager>(),
            [.. container.Resolve<IBlockValidationModule[]>(), probe],
            LimboLogs.Instance);

        using IWitnessGeneratingBlockProcessingEnvScope scope = factory.CreateScope();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(probe.WorldState, Is.Not.Null);
            Assert.That(probe.WorldState, Is.Not.InstanceOf<IBlockAccessListSource>());
        }
    }

    /// <summary>Captures the world state the env scope resolves, which the factory does not otherwise expose.</summary>
    private sealed class WorldStateProbeModule : Autofac.Module, IBlockValidationModule
    {
        public IWorldState? WorldState { get; private set; }

        protected override void Load(ContainerBuilder builder) =>
            builder.OnBuild(scope => WorldState = scope.Resolve<IWorldState>());
    }
}
