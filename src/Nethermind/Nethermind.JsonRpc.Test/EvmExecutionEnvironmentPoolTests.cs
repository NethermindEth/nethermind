// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Core;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Facade;
using Nethermind.Facade.Proxy.Models.Simulate;
using Nethermind.Facade.Simulate;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test;

[Parallelizable(ParallelScope.All)]
public class EvmExecutionEnvironmentPoolTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    // The EVM admission gate runs up to EthModuleConcurrentInstances gated calls at once. A pool that held fewer environments
    // would refuse a call after the gate admitted it.
    [Test]
    public async Task Simulate_environment_pool_holds_every_call_the_admission_gate_runs_at_once([Values(1, 3)] int ethModuleConcurrentInstances)
    {
        JsonRpcConfig config = new() { EthModuleConcurrentInstances = ethModuleConcurrentInstances };
        int maxConcurrent = config.GetEvmExecutionSlots();
        using SemaphoreSlim entered = new(0);
        using ManualResetEventSlim release = new();
        ISimulateReadOnlyBlocksProcessingEnvFactory envFactory = Substitute.For<ISimulateReadOnlyBlocksProcessingEnvFactory>();
        envFactory.Create().Returns(_ =>
        {
            ISimulateReadOnlyBlocksProcessingEnv env = Substitute.For<ISimulateReadOnlyBlocksProcessingEnv>();
            // Holds the environment until the test lets go, then fails the call.
            env.Begin(Arg.Any<BlockHeader?>()).Returns<SimulateReadOnlyBlocksProcessingScope>(_ =>
            {
                entered.Release();
                release.Wait(TestTimeout);
                throw new InvalidOperationException("released");
            });
            return env;
        });
        using IContainer container = new ContainerBuilder()
            .AddModule(new TestNethermindModule(config))
            .AddSingleton<ISimulateReadOnlyBlocksProcessingEnvFactory>(envFactory)
            .Build();
        IBlockchainBridge bridge = container.Resolve<IBlockchainBridgeFactory>().CreateBlockchainBridge();

        Task[] running = [.. Enumerable.Range(0, maxConcurrent).Select(_ => Task.Run(() => Simulate(bridge)))];
        try
        {
            for (int i = 1; i <= maxConcurrent; i++)
            {
                Assert.That(await entered.WaitAsync(TestTimeout), Is.True, $"call {i} of {maxConcurrent} got an environment");
            }

            Assert.That(() => Simulate(bridge), Throws.InstanceOf<ConcurrencyLimitReachedException>(), "one call more is refused");
        }
        finally
        {
            release.Set();
        }

        foreach (Task call in running)
        {
            Assert.That(async () => await call.WaitAsync(TestTimeout), Throws.InvalidOperationException.With.Message.EqualTo("released"));
        }
    }

    private static void Simulate(IBlockchainBridge bridge) =>
        bridge.Simulate(Build.A.EmptyBlockHeader, new SimulatePayload<TransactionWithSourceDetails>(),
            Substitute.For<ISimulateBlockTracerFactory<object>>(), 10_000_000, CancellationToken.None);
}
