// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#pragma warning disable IDE0290 // Test step classes have unused DI parameters by design

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Api.Steps;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Headers;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Config;
using Nethermind.Consensus.AuRa.InitializationSteps;
using Nethermind.Core;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Modules;
using Nethermind.Init.Steps;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Specs;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.State.Repositories;
using NSubstitute;
using NUnit.Framework;
using CoreBuild = Nethermind.Core.Test.Builders.Build;

namespace Nethermind.Runner.Test.Ethereum.Steps
{
    [TestFixture, Parallelizable(ParallelScope.All)]
    public class EthereumStepsManagerTests
    {
        [TestCase(true, true, true, true)]
        [TestCase(true, true, false, true)]
        [TestCase(true, false, false, false)]
        [TestCase(false, true, true, true)]
        [TestCase(false, true, true, false)]
        [TestCase(false, true, false, true)]
        [TestCase(false, true, false, false)]
        [TestCase(false, false, false, false)]
        [TestCase(true, true, true, true, true)]
        [TestCase(true, true, false, true, true)]
        [TestCase(true, false, false, false, true)]
        public async Task Warmup_selects_head_before_pivot(bool hasHead, bool hasPivot, bool storedPivot, bool hasPivotHash, bool genesisOnly = false)
        {
            const ulong headTimestamp = MainnetSpecProvider.PragueBlockTimestamp;
            const ulong pivotTimestamp = MainnetSpecProvider.OsakaBlockTimestamp;
            const ulong now = MainnetSpecProvider.BPO2BlockTimestamp;
            Block pivot = CoreBuild.A.Block.WithNumber(25_000_000).WithTimestamp(pivotTimestamp).TestObject;
            SyncConfig syncConfig = new()
            {
                FastSync = true,
                PivotNumber = hasPivot ? pivot.Number : 0,
                PivotHash = hasPivotHash ? pivot.Hash!.ToString() : null
            };
            using IContainer container = CreateWarmupEnvironment(syncConfig, now);
            IBlockTree tree = container.Resolve<IBlockTree>();
            if (hasHead)
            {
                Block genesis = CoreBuild.A.Block.Genesis.WithTimestamp(MainnetSpecProvider.GenesisBlockTimestamp).TestObject;
                tree.SuggestBlock(genesis);
                Assert.That(tree.TryUpdateMainChain(genesis.Header, true, preloadedBlocks: [genesis]), Is.True);
                if (!genesisOnly)
                {
                    Block head = CoreBuild.A.Block.WithParent(genesis).WithNumber(1).WithTimestamp(headTimestamp).TestObject;
                    tree.SuggestBlock(head);
                    Assert.That(tree.TryUpdateMainChain(head.Header, true, preloadedBlocks: [genesis, head]), Is.True);
                }
            }
            if (storedPivot)
                tree.Insert(pivot.Header, BlockTreeInsertHeaderOptions.TotalDifficultyNotNeeded);

            using ILifetimeScope restartedScope = container.BeginLifetimeScope(builder => builder
                .AddSingleton<IBlockTree, BlockTree>()
                .AddSingleton<EvmWarmer>());
            if (hasHead && genesisOnly)
                Assert.That(restartedScope.Resolve<IBlockTree>().Head?.Timestamp, Is.EqualTo(MainnetSpecProvider.GenesisBlockTimestamp));
            EvmWarmer warmer = restartedScope.Resolve<EvmWarmer>();
            ForkActivation expected = hasHead && !genesisOnly ? (1, headTimestamp)
                : hasPivot ? (pivot.Number, storedPivot ? pivotTimestamp : now)
                : (0, hasHead ? MainnetSpecProvider.GenesisBlockTimestamp : 0);
            Assert.That(warmer.GetWarmupActivation(), Is.EqualTo(expected));
            await warmer.Execute(CancellationToken.None);
        }

        [Test]
        public void Warmup_reads_pivot_timestamp_without_creating_a_chain_level()
        {
            BlockHeader pivot = CoreBuild.A.BlockHeader.WithNumber(25_000_000)
                .WithTimestamp(MainnetSpecProvider.OsakaBlockTimestamp).TestObject;
            SyncConfig syncConfig = new() { FastSync = true, PivotNumber = pivot.Number, PivotHash = pivot.Hash!.ToString() };
            using IContainer container = CreateWarmupEnvironment(syncConfig, MainnetSpecProvider.BPO2BlockTimestamp);
            EvmWarmer warmer = container.Resolve<EvmWarmer>();
            container.Resolve<IHeaderStore>().Insert(pivot);
            IChainLevelInfoRepository levels = container.Resolve<IChainLevelInfoRepository>();
            Assert.That(levels.LoadLevel(pivot.Number), Is.Null);

            ForkActivation activation = warmer.GetWarmupActivation();

            using (Assert.EnterMultipleScope())
            {
                Assert.That(activation, Is.EqualTo(new ForkActivation(pivot.Number, pivot.Timestamp)));
                Assert.That(levels.LoadLevel(pivot.Number), Is.Null);
            }
        }

        private static IContainer CreateWarmupEnvironment(SyncConfig syncConfig, ulong now) => new ContainerBuilder()
            .AddModule(new TestNethermindModule(syncConfig))
            .AddSingleton<ITimestamper>(new ManualTimestamper(DateTimeOffset.FromUnixTimeSeconds((long)now).UtcDateTime))
            .AddSingleton<EvmWarmer>()
            .Build();

        [Test]
        public async Task When_no_assemblies_defined()
        {
            await using IContainer container = CreateNethermindEnvironment();
            EthereumStepsManager stepsManager = container.Resolve<EthereumStepsManager>();

            using CancellationTokenSource source = new(TimeSpan.FromSeconds(1));
            await stepsManager.InitializeAll(source.Token);
        }

        [Test]
        public async Task With_steps_from_here_AuRa()
        {
            await using IContainer container = CreateAuraApi(
                typeof(StepCStandard),
                typeof(StepCAuRa)
            );

            EthereumStepsManager stepsManager = container.Resolve<EthereumStepsManager>();

            Assert.That(async () => await stepsManager.InitializeAll(CancellationToken.None),
                Throws.TypeOf<TestException>());
        }

        [Test]
        public async Task With_failing_steps()
        {
            await using IContainer container = CreateNethermindEnvironment(
                new StepInfo(typeof(StepForever))
            );

            EthereumStepsManager stepsManager = container.Resolve<EthereumStepsManager>();
            using CancellationTokenSource source = new(TimeSpan.FromSeconds(1));
            try
            {
                await stepsManager.InitializeAll(source.Token);
            }
            catch (Exception e)
            {
                if (!(e is OperationCanceledException))
                {
                    Assert.Fail($"Exception should be {nameof(OperationCanceledException)}. Received {e}");
                }
            }
        }

        [Test]
        public async Task Should_Unwrap_InvalidConfigurationException()
        {
            await using IContainer container = CreateNethermindEnvironment(
                new StepInfo(typeof(FailedConstructorWithInvalidConfigurationStep))
            );

            EthereumStepsManager stepsManager = container.Resolve<EthereumStepsManager>();
            using CancellationTokenSource source = new(TimeSpan.FromSeconds(1));

            Func<Task> act = () => stepsManager.InitializeAll(source.Token);
            Assert.That(async () => await act(), Throws.TypeOf<InvalidConfigurationException>());
        }

        [Test]
        public async Task With_constructor_without_nethermind_api()
        {
            await using IContainer container = CreateNethermindEnvironment(
                new StepInfo(typeof(StepWithLogManagerInConstructor))
            );

            EthereumStepsManager stepsManager = container.Resolve<EthereumStepsManager>();
            using CancellationTokenSource source = new(TimeSpan.FromSeconds(1));
            await stepsManager.InitializeAll(source.Token);

            Assert.That(container.Resolve<StepWithLogManagerInConstructor>().WasExecuted, Is.True);
        }

        [Test]
        public async Task With_ambiguous_steps()
        {
            await using IContainer container = CreateNethermindEnvironment(
                new StepInfo(typeof(StepWithLogManagerInConstructor)),
                new StepInfo(typeof(StepWithSameBaseStep))
            );

            EthereumStepsManager stepsManager = container.Resolve<EthereumStepsManager>();
            using CancellationTokenSource source = new(TimeSpan.FromSeconds(1));
            Func<Task> act = async () => await stepsManager.InitializeAll(source.Token);
            Assert.That(async () => await act(), Throws.TypeOf<StepDependencyException>());
        }

        [Test]
        [CancelAfter(1000)]
        public async Task With_dependent_step(CancellationToken cancellationToken)
        {
            await using IContainer container = CreateNethermindEnvironment(
                new StepInfo(typeof(StepB)),
                new StepInfo(typeof(StepCStandard)),
                new StepInfo(typeof(StepE))
            );

            EthereumStepsManager stepsManager = container.Resolve<EthereumStepsManager>();
            Task initTask = stepsManager.InitializeAll(cancellationToken);
            await Task.Delay(100, cancellationToken);
            Assert.That(initTask.IsCompleted, Is.False);

            Assert.That(container.Resolve<StepB>().WasExecuted, Is.False);
            container.Resolve<StepE>().Waiter.SetResult();
            await initTask;

            Assert.That(container.Resolve<StepB>().WasExecuted, Is.True);
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
                .AddSingleton(new EthereumJsonSerializer())
                .Bind<IJsonSerializer, EthereumJsonSerializer>()
                .AddSingleton<ILogManager>(LimboLogs.Instance)
                .AddSingleton<ChainSpec>(new ChainSpec())
                .AddSingleton<ISpecProvider>(Substitute.For<ISpecProvider>())
                .AddSingleton<IProcessExitSource>(Substitute.For<IProcessExitSource>())
                .AddSingleton<IDisposableStack, AutofacDisposableStack>()
                .AddSingleton<IEthereumStepsLoader, EthereumStepsLoader>()
                .AddSingleton<EthereumStepsManager>()
                .AddSingleton<ILogManager>(LimboLogs.Instance);

            foreach (StepInfo stepInfo in stepInfos)
            {
                builder.AddStep(stepInfo);
            }

            return builder;
        }
    }

    public class StepLong : IStep
    {
        public async Task Execute(CancellationToken cancellationToken) => await Task.Delay(100000, cancellationToken);

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
        public override Task Execute(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    public class StepForever : IStep
    {
        public async Task Execute(CancellationToken cancellationToken) => await Task.Delay(100000, cancellationToken);

        public StepForever(NethermindApi runnerContext)
        {
        }
    }

    public class StepA : IStep
    {
        public Task Execute(CancellationToken cancellationToken) => Task.CompletedTask;

        public StepA(NethermindApi runnerContext)
        {
        }
    }

    [RunnerStepDependencies(typeof(StepC))]
    public class StepB : IStep
    {
        public bool WasExecuted = false;

        public Task Execute(CancellationToken cancellationToken)
        {
            WasExecuted = true;
            return Task.CompletedTask;
        }

        public StepB(NethermindApi runnerContext)
        {
        }
    }

    public abstract class StepC : IStep
    {
        public virtual Task Execute(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    public abstract class StepD : IStep
    {
        public virtual Task Execute(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    [RunnerStepDependencies(dependencies: [], dependents: [typeof(StepB)])]
    public class StepE : IStep
    {
        public TaskCompletionSource Waiter = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public virtual Task Execute(CancellationToken cancellationToken) => Waiter.Task;
    }

    /// <summary>
    /// Designed to fail
    /// </summary>
    public class StepCAuRa : StepC
    {
        public StepCAuRa(AuRaNethermindApi runnerContext)
        {
        }

        public override async Task Execute(CancellationToken cancellationToken) => await Task.Run(static () => throw new TestException());
    }

    public class StepCStandard : StepC
    {
        public StepCStandard(NethermindApi runnerContext)
        {
        }
    }

    public class FailedConstructorWithInvalidConfigurationStep : StepC
    {
        public FailedConstructorWithInvalidConfigurationStep() => throw new InvalidConfigurationException("Invalid config", -1);
    }

    class TestException : Exception
    {
    }
}
