// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Autofac.Core;
using FluentAssertions;
using FluentAssertions.Execution;
using Nethermind.Api;
using Nethermind.Init.Steps;
using Nethermind.Logging;
using Nethermind.Runner.Modules;
using NUnit.Framework;

namespace Nethermind.Runner.Test.Ethereum.Steps
{
    [TestFixture, Parallelizable(ParallelScope.All)]
    public class EthereumStepsManagerTests
    {
        [Test]
        public void When_no_steps_defined()
        {
            IContainer runnerContext = CreateEmptyContainer();
            Action action = () => runnerContext.Resolve<EthereumStepsManager>();
            action.Should().Throw<DependencyResolutionException>();
        }

        [Test]
        public async Task With_steps_from_here()
        {
            IContainer runnerContext = CreateNethermindApi();
            EthereumStepsManager stepsManager = runnerContext.Resolve<EthereumStepsManager>();

            using CancellationTokenSource source = new CancellationTokenSource(TimeSpan.FromSeconds(1));

            try
            {
                await stepsManager.InitializeAll(source.Token);
            }
            catch (Exception e)
            {
                if (!(e is OperationCanceledException))
                {
                    throw new AssertionFailedException($"Exception should be {nameof(OperationCanceledException)}");
                }
            }
        }

        [Test]
        [Retry(3)]
        public async Task With_steps_from_here_AuRa()
        {
            IContainer runnerContext = CreateAuraApi();
            EthereumStepsManager stepsManager = runnerContext.Resolve<EthereumStepsManager>();

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
            IContainer runnerContext = CreateNethermindApi();
            EthereumStepsManager stepsManager = runnerContext.Resolve<EthereumStepsManager>();

            using CancellationTokenSource source = new CancellationTokenSource(TimeSpan.FromSeconds(2));

            try
            {
                await stepsManager.InitializeAll(source.Token);
            }
            catch (Exception e)
            {
                if (!(e is OperationCanceledException))
                {
                    throw new AssertionFailedException($"Exception should be {nameof(OperationCanceledException)}");
                }
            }
        }

        private static ContainerBuilder CreateBaseContainerBuilder()
        {
            var builder = new ContainerBuilder();
            builder.RegisterInstance(LimboLogs.Instance).AsImplementedInterfaces();
            builder.RegisterModule(new BaseModule());
            builder.RegisterModule(new RunnerModule());
            return builder;
        }

        private static IContainer CreateEmptyContainer()
        {
            return CreateBaseContainerBuilder()
                .Build();
        }

        private static IContainer CreateNethermindApi()
        {
            ContainerBuilder builder = CreateBaseContainerBuilder();
            builder.RegisterIStep(typeof(StepLong));
            builder.RegisterIStep(typeof(StepForever));
            builder.RegisterIStep(typeof(StepA));
            builder.RegisterIStep(typeof(StepB));
            builder.RegisterIStep(typeof(StepCStandard));
            return builder.Build();
        }
        private static IContainer CreateAuraApi()
        {
            ContainerBuilder builder = CreateBaseContainerBuilder();
            builder.RegisterIStep(typeof(StepLong));
            builder.RegisterIStep(typeof(StepForever));
            builder.RegisterIStep(typeof(StepA));
            builder.RegisterIStep(typeof(StepB));
            builder.RegisterIStep(typeof(StepCAuRa));
            return builder.Build();
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

    public class StepForever : IStep
    {
        public async Task Execute(CancellationToken cancellationToken)
        {
            await Task.Delay(100000);
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
        public StepCAuRa()
        {
        }

        public override async Task Execute(CancellationToken cancellationToken)
        {
            await Task.Run(() => throw new TestException());
        }
    }

    public class StepCStandard : StepC
    {
        public StepCStandard()
        {
        }
    }

    class TestException : Exception
    {
    }
}
