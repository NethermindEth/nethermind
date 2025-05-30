// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using FluentAssertions;
using FluentAssertions.Execution;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Api.Steps;
using Nethermind.Config;
using Nethermind.Consensus.AuRa.InitializationSteps;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Init.Steps;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Specs.ChainSpecStyle;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Runner.Test.Ethereum.Steps
{
    [TestFixture, Parallelizable(ParallelScope.All)]
    public class EthereumStepsManagerTests
    {
        [Test]
        public async Task When_no_assemblies_defined()
        {
            await using IContainer container = CreateNethermindEnvironment();
            EthereumStepsManager stepsManager = container.Resolve<EthereumStepsManager>();

            using CancellationTokenSource source = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            await stepsManager.InitializeAll(source.Token);
        }

        [Test]
        [Retry(3)]
        public async Task With_steps_from_here_AuRa()
        {
            await using IContainer container = CreateAuraApi(
                typeof(StepCStandard),
                typeof(StepCAuRa)
            );

            EthereumStepsManager stepsManager = container.Resolve<EthereumStepsManager>();

            using CancellationTokenSource source = new CancellationTokenSource(TimeSpan.FromSeconds(5));

            try
            {
                await stepsManager.InitializeAll(source.Token);
            }
            catch (Exception e)
            {
                e.Should().BeOfType<TestException>();
            }
        }

        [Test]
        public async Task With_failing_steps()
        {
            await using IContainer container = CreateNethermindEnvironment(
                new StepInfo(typeof(StepForever))
            );

            EthereumStepsManager stepsManager = container.Resolve<EthereumStepsManager>();
            using CancellationTokenSource source = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            try
            {
                await stepsManager.InitializeAll(source.Token);
            }
            catch (Exception e)
            {
                if (!(e is OperationCanceledException))
                {
                    throw new AssertionFailedException($"Exception should be {nameof(OperationCanceledException)}. Received {e}");
                }
            }
        }

        [Test]
        public async Task With_constructor_without_nethermind_api()
        {
            await using IContainer container = CreateNethermindEnvironment(
                new StepInfo(typeof(StepWithLogManagerInConstructor))
            );

            EthereumStepsManager stepsManager = container.Resolve<EthereumStepsManager>();
            using CancellationTokenSource source = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            await stepsManager.InitializeAll(source.Token);

            container.Resolve<StepWithLogManagerInConstructor>().WasExecuted.Should().BeTrue();
        }

        [Test]
        public async Task With_ambigious_steps()
        {
            await using IContainer container = CreateNethermindEnvironment(
                new StepInfo(typeof(StepWithLogManagerInConstructor)),
                new StepInfo(typeof(StepWithSameBaseStep))
            );

            EthereumStepsManager stepsManager = container.Resolve<EthereumStepsManager>();
            using CancellationTokenSource source = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            var act = async () => await stepsManager.InitializeAll(source.Token);
            await act.Should().ThrowAsync<StepDependencyException>();
        }

        private static IContainer CreateNethermindEnvironment(params IEnumerable<StepInfo> stepInfos)
        {
            IConsensusPlugin consensusPlugin = Substitute.For<IConsensusPlugin>();
            consensusPlugin.ApiType.ReturnsForAnyArgs(typeof(NethermindApi));

            return CreateCommonBuilder(stepInfos)
                .AddSingleton<IConsensusPlugin>(consensusPlugin)
                .Bind<INethermindApi, NethermindApi>()
                .Build();
        }

        private static IContainer CreateAuraApi(params IEnumerable<StepInfo> stepInfos)
        {
            IConsensusPlugin consensusPlugin = Substitute.For<IConsensusPlugin>();
            consensusPlugin.ApiType.ReturnsForAnyArgs(typeof(AuRaNethermindApi));

            return CreateCommonBuilder(stepInfos)
                .AddSingleton<AuRaNethermindApi>()
                .AddSingleton<IConsensusPlugin>(consensusPlugin)
                .Bind<INethermindApi, AuRaNethermindApi>()
                .Build();
        }

        private static ContainerBuilder CreateCommonBuilder(params IEnumerable<StepInfo> stepInfos)
        {
            ContainerBuilder builder = new ContainerBuilder()
                .AddSingleton<INethermindApi, NethermindApi>()
                .AddSingleton<NethermindApi.Dependencies>()
                .AddSingleton<IConfigProvider>(new ConfigProvider())
                .AddSingleton<IJsonSerializer>(new EthereumJsonSerializer())
                .AddSingleton<ILogManager>(LimboLogs.Instance)
                .AddSingleton<ChainSpec>(new ChainSpec())
                .AddSingleton<ISpecProvider>(Substitute.For<ISpecProvider>())
                .AddSingleton<IProcessExitSource>(Substitute.For<IProcessExitSource>())
                .AddSingleton<IDisposableStack, AutofacDisposableStack>()
                .AddSingleton<IEthereumStepsLoader, EthereumStepsLoader>()
                .AddSingleton<EthereumStepsManager>()
                .AddSingleton<ILogManager>(LimboLogs.Instance);

            foreach (var stepInfo in stepInfos)
            {
                builder.AddStep(stepInfo);
            }

            return builder;
        }
    }

    public class StepLong : IStep
    {
        public async Task Execute(CancellationToken cancellationToken)
        {
            await Task.Delay(100000, cancellationToken);
        }

        public StepLong(NethermindApi runnerContext)
        {
        }
    }


    public abstract class BaseStep : IStep
    {
        public abstract Task Execute(CancellationToken cancellationToken);
    }

#pragma warning disable CS9113 // Parameter is unread.
    public class StepWithLogManagerInConstructor(ILogManager _) : BaseStep
#pragma warning restore CS9113 // Parameter is unread.
    {
        public bool WasExecuted { get; set; }

        public override Task Execute(CancellationToken cancellationToken)
        {
            WasExecuted = true;
            return Task.CompletedTask;
        }
    }

    public class StepWithSameBaseStep() : BaseStep
    {
        public override Task Execute(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }

    public class StepForever : IStep
    {
        public async Task Execute(CancellationToken cancellationToken)
        {
            await Task.Delay(100000, cancellationToken);
        }

        public StepForever(NethermindApi runnerContext)
        {
        }
    }

    public class StepA : IStep
    {
        public Task Execute(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public StepA(NethermindApi runnerContext)
        {
        }
    }

    [RunnerStepDependencies(typeof(StepC))]
    public class StepB : IStep
    {
        public Task Execute(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public StepB(NethermindApi runnerContext)
        {
        }
    }

    public abstract class StepC : IStep
    {
        public virtual Task Execute(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }

    public abstract class StepD : IStep
    {
        public virtual Task Execute(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Designed to fail
    /// </summary>
    public class StepCAuRa : StepC
    {
        public StepCAuRa(AuRaNethermindApi runnerContext)
        {
        }

        public override async Task Execute(CancellationToken cancellationToken)
        {
            await Task.Run(static () => throw new TestException());
        }
    }

    public class StepCStandard : StepC
    {
        public StepCStandard(NethermindApi runnerContext)
        {
        }
    }

    class TestException : Exception
    {
    }
}
