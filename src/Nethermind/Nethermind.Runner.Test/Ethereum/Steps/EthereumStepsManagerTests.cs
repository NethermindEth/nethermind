// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#pragma warning disable IDE0290 // Test step classes have unused DI parameters by design

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
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
using Nethermind.State.OverridableEnv;
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
                SnapSync = true,
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
            SyncConfig syncConfig = new() { FastSync = true, SnapSync = true, PivotNumber = pivot.Number, PivotHash = pivot.Hash!.ToString() };
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

        [Test]
        public async Task Warmup_does_nothing_when_disabled()
        {
            IOverridableEnvFactory envFactory = Substitute.For<IOverridableEnvFactory>();
            using IContainer container = new ContainerBuilder()
                .AddModule(new TestNethermindModule(new SyncConfig(), new InitConfig { EvmWarmupEnabled = false }))
                .AddSingleton(envFactory)
                .AddSingleton<EvmWarmer>()
                .Build();

            await container.Resolve<EvmWarmer>().Execute(CancellationToken.None);

            envFactory.DidNotReceive().Create();
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

        private static readonly StepInfo[] _targetGraph =
            [typeof(StepA), typeof(StepB), typeof(StepCStandard), typeof(StepE), typeof(CommandStep)];

        private static IEnumerable<TestCaseData> TargetCases()
        {
            yield return new TestCaseData(typeof(StepB), null, new[] { typeof(StepB), typeof(StepC), typeof(StepE) })
                .SetName("Target pulls in its declared dependency and an inverted dependents edge");
            yield return new TestCaseData(typeof(StepCStandard), null, new[] { typeof(StepC) })
                .SetName("Target given as the concrete implementation resolves to its base slot");
            yield return new TestCaseData(typeof(StepA), null, new[] { typeof(StepA) })
                .SetName("Dependency-free target runs alone");
            yield return new TestCaseData(null, null, new[] { typeof(StepA), typeof(StepB), typeof(StepC), typeof(StepE) })
                .SetName("Without a target every step but the command step runs");
            yield return new TestCaseData(null, CommandStep.CommandName, new[] { typeof(CommandStep) })
                .SetName("Command name selects the command step");
        }

        [TestCaseSource(nameof(TargetCases))]
        [CancelAfter(5000)]
        public async Task Only_the_target_closure_runs(Type? target, string? command, Type[] expectedExecuted)
        {
            await using IContainer container = CreateNethermindEnvironment(target, command, _targetGraph);

            // StepE blocks until released; it is in the graph to prove an inverted `dependents:` edge is followed.
            container.Resolve<StepE>().Waiter.SetResult();

            await container.Resolve<EthereumStepsManager>().InitializeAll(CancellationToken.None);

            Type[] executed = [.. _targetGraph
                .Where(step => ((IRecordingStep)container.Resolve(step.StepType)).WasExecuted)
                .Select(step => step.StepBaseType)];

            Assert.That(executed, Is.EquivalentTo(expectedExecuted));
        }

        [Test]
        [CancelAfter(5000)]
        public async Task Target_that_completes_leaves_the_run_successful([Values] bool hasTarget, CancellationToken cancellationToken)
        {
            await using IContainer container = CreateNethermindEnvironment(
                hasTarget ? typeof(StepA) : null, command: null, [typeof(StepA)]);

            await container.Resolve<EthereumStepsManager>().InitializeAll(cancellationToken);

            Assert.That(container.Resolve<StepA>().WasExecuted, Is.True);
        }

        [Test]
        [CancelAfter(5000)]
        public async Task Target_that_does_not_complete_fails_the_run(CancellationToken cancellationToken)
        {
            // A TaskCanceledException raised inside a step leaves its task Canceled rather than Faulted, and
            // nothing requested cancellation, so without the outcome check the run would report success.
            await using IContainer container = CreateNethermindEnvironment(
                typeof(SelfCancellingStep), command: null, [typeof(SelfCancellingStep)]);

            Assert.That(async () => await container.Resolve<EthereumStepsManager>().InitializeAll(cancellationToken),
                Throws.TypeOf<StepDependencyException>());
        }

        [Test]
        public async Task Target_run_interrupted_by_shutdown_does_not_report_success()
        {
            await using IContainer container = CreateNethermindEnvironment(
                typeof(StepForever), command: null, [typeof(StepForever)]);
            using CancellationTokenSource cancellation = new(TimeSpan.FromMilliseconds(200));

            Assert.That(async () => await container.Resolve<EthereumStepsManager>().InitializeAll(cancellation.Token),
                Throws.InstanceOf<OperationCanceledException>());
        }

        [Test]
        [CancelAfter(5000)]
        public async Task A_failing_ancestor_fails_the_run_with_its_own_error(CancellationToken cancellationToken)
        {
            // StepCAuRa throws; StepB depends on StepC, so the target is cancelled by the ancestor rather than
            // faulting itself. The ancestor's exception is the useful one to surface.
            await using IContainer container = CreateAuraApi(
                typeof(StepB), command: null, [typeof(StepB), typeof(StepCAuRa)]);

            Assert.That(async () => await container.Resolve<EthereumStepsManager>().InitializeAll(cancellationToken),
                Throws.TypeOf<TestException>());
        }

        [Test]
        public async Task Unknown_command_reports_the_available_ones()
        {
            await using IContainer container = CreateNethermindEnvironment(target: null, command: "no-such-command", _targetGraph);

            Assert.That(async () => await container.Resolve<EthereumStepsManager>().InitializeAll(CancellationToken.None),
                Throws.TypeOf<InvalidConfigurationException>()
                    .With.Message.Contains(CommandStep.CommandName)
                    .And.Property(nameof(InvalidConfigurationException.ExitCode)).EqualTo(ExitCodes.UnrecognizedOption));
        }

        [Test]
        public async Task Unregistered_target_names_the_registered_steps()
        {
            await using IContainer container = CreateNethermindEnvironment(typeof(StepA), command: null, [typeof(StepB), typeof(StepCStandard)]);

            Assert.That(async () => await container.Resolve<EthereumStepsManager>().InitializeAll(CancellationToken.None),
                Throws.TypeOf<StepDependencyException>().With.Message.Contains(nameof(StepB)));
        }

        [Test]
        [CancelAfter(5000)]
        public async Task Config_and_command_selecting_the_same_step_is_not_a_conflict(CancellationToken cancellationToken)
        {
            // The production path for a config-backed command: the command names the step and the configuration
            // that the job needs selects the very same one.
            await using IContainer container = CreateNethermindEnvironment(
                typeof(CommandStep), CommandStep.CommandName, _targetGraph);

            container.Resolve<StepE>().Waiter.SetResult();
            await container.Resolve<EthereumStepsManager>().InitializeAll(cancellationToken);

            Assert.That(container.Resolve<CommandStep>().WasExecuted, Is.True);
        }

        [Test]
        public async Task Two_distinct_targets_are_rejected()
        {
            await using IContainer container = CreateNethermindEnvironment(typeof(StepA), CommandStep.CommandName, _targetGraph);

            Assert.That(async () => await container.Resolve<EthereumStepsManager>().InitializeAll(CancellationToken.None),
                Throws.TypeOf<InvalidConfigurationException>()
                    .With.Property(nameof(InvalidConfigurationException.ExitCode)).EqualTo(ExitCodes.ConflictingConfigurations));
        }

        [Test]
        [CancelAfter(5000)]
        public async Task Pruned_steps_are_never_started(CancellationToken cancellationToken)
        {
            // StepForever would never complete, so reaching the end proves it was not merely side-effect free.
            await using IContainer container = CreateNethermindEnvironment(
                typeof(StepA), command: null, [typeof(StepA), typeof(StepForever)]);

            await container.Resolve<EthereumStepsManager>().InitializeAll(cancellationToken);

            Assert.That(container.Resolve<StepA>().WasExecuted, Is.True);
        }

        private static IContainer CreateNethermindEnvironment(params IEnumerable<StepInfo> stepInfos) =>
            CreateNethermindEnvironment(target: null, command: null, stepInfos);

        private static IContainer CreateNethermindEnvironment(Type? target, string? command, IEnumerable<StepInfo> stepInfos)
        {
            IConsensusPlugin consensusPlugin = Substitute.For<IConsensusPlugin>();
            consensusPlugin.ApiType.ReturnsForAnyArgs(typeof(NethermindApi));

            ContainerBuilder builder = CreateCommonBuilder(stepInfos)
                .AddSingleton<IConsensusPlugin>(consensusPlugin)
                .Bind<INethermindApi, NethermindApi>();

            if (target is not null) builder.SelectStepTarget(target);
            if (command is not null) builder.AddSingleton(new StepCommandSelection(command));

            return builder.Build();
        }

        private static IContainer CreateAuraApi(params IEnumerable<StepInfo> stepInfos) =>
            CreateAuraApi(target: null, command: null, stepInfos);

        private static IContainer CreateAuraApi(Type? target, string? command, IEnumerable<StepInfo> stepInfos)
        {
            IConsensusPlugin consensusPlugin = Substitute.For<IConsensusPlugin>();
            consensusPlugin.ApiType.ReturnsForAnyArgs(typeof(AuRaNethermindApi));

            ContainerBuilder builder = CreateCommonBuilder(stepInfos)
                .AddSingleton<AuRaNethermindApi>()
                .AddSingleton<IConsensusPlugin>(consensusPlugin)
                .Bind<INethermindApi, AuRaNethermindApi>();

            if (target is not null) builder.SelectStepTarget(target);
            if (command is not null) builder.AddSingleton(new StepCommandSelection(command));

            return builder.Build();
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

    /// <summary>Ends Canceled without anyone requesting cancellation, e.g. an HTTP timeout inside a step.</summary>
    public class SelfCancellingStep : IStep
    {
        public Task Execute(CancellationToken cancellationToken) => throw new TaskCanceledException();
    }

    public class StepForever : IStep
    {
        public async Task Execute(CancellationToken cancellationToken) => await Task.Delay(100000, cancellationToken);

        public StepForever(NethermindApi runnerContext)
        {
        }
    }

    /// <summary>Lets a test assert which steps of a graph actually ran.</summary>
    public interface IRecordingStep
    {
        bool WasExecuted { get; }
    }

    public class StepA : IStep, IRecordingStep
    {
        public bool WasExecuted { get; private set; }

        public Task Execute(CancellationToken cancellationToken)
        {
            WasExecuted = true;
            return Task.CompletedTask;
        }

        public StepA(NethermindApi runnerContext)
        {
        }
    }

    [RunnerStepDependencies(typeof(StepC))]
    public class StepB : IStep, IRecordingStep
    {
        public bool WasExecuted { get; private set; }

        public Task Execute(CancellationToken cancellationToken)
        {
            WasExecuted = true;
            return Task.CompletedTask;
        }

        public StepB(NethermindApi runnerContext)
        {
        }
    }

    public abstract class StepC : IStep, IRecordingStep
    {
        public bool WasExecuted { get; private set; }

        public virtual Task Execute(CancellationToken cancellationToken)
        {
            WasExecuted = true;
            return Task.CompletedTask;
        }
    }

    [StepCommand(CommandStep.CommandName, "A step that only runs when it is the target.")]
    public class CommandStep : IStep, IRecordingStep
    {
        public const string CommandName = "test-command";

        public bool WasExecuted { get; private set; }

        public Task Execute(CancellationToken cancellationToken)
        {
            WasExecuted = true;
            return Task.CompletedTask;
        }
    }

    public abstract class StepD : IStep
    {
        public virtual Task Execute(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    [RunnerStepDependencies(dependencies: [], dependents: [typeof(StepB)])]
    public class StepE : IStep, IRecordingStep
    {
        public TaskCompletionSource Waiter = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool WasExecuted { get; private set; }

        public virtual async Task Execute(CancellationToken cancellationToken)
        {
            WasExecuted = true;
            await Waiter.Task;
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
