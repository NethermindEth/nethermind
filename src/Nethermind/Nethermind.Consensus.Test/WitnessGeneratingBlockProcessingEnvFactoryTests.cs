// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using Autofac;
using Nethermind.Consensus.Stateless;
using Nethermind.Core;
using Nethermind.Core.Container;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Modules;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Logging;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using Nethermind.State;
using NUnit.Framework;

namespace Nethermind.Consensus.Test;

public class WitnessGeneratingBlockProcessingEnvFactoryTests
{
    // proof_call runs its call through this env, so a POST_TX frame must find a diff recorder here
    // exactly as it does under eth_call, or the EIP-7906 assertion opcodes halt with BadInstruction.
    [TestCase(false, TestName = "CreateScope_ForkNeverScheduled_NoDiffRecorder")]
    [TestCase(true, TestName = "CreateScope_ForkScheduled_CarriesADiffRecorder")]
    public void CreateScope_DiffRecorderFollowsTheForkSchedule(bool schedulesEip7906)
    {
        using IContainer container = new ContainerBuilder()
            .AddModule(new TestNethermindModule(new OverridableReleaseSpec(Cancun.Instance)
            {
                IsEip7906Enabled = schedulesEip7906,
                IsEip7928Enabled = schedulesEip7906
            }))
            .Build();

        WorldStateProbeModule probe = new();
        using WitnessGeneratingBlockProcessingEnvFactory factory = new(
            container.Resolve<ILifetimeScope>(),
            container.Resolve<IWorldStateManager>(),
            container.Resolve<IDbProvider>(),
            [.. container.Resolve<IBlockValidationModule[]>(), probe],
            container.Resolve<ISpecProvider>(),
            LimboLogs.Instance);

        using IWitnessGeneratingBlockProcessingEnvScope scope = factory.CreateScope();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(probe.WorldState, Is.Not.Null);
            Assert.That(probe.WorldState is IBlockAccessListSource, Is.EqualTo(schedulesEip7906));
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
