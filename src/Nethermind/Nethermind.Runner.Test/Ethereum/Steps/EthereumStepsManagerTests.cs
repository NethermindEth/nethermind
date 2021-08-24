//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions.Execution;
using Nethermind.Api;
using Nethermind.Config;
using Nethermind.Consensus.AuRa.InitializationSteps;
using Nethermind.Init.Steps;
using Nethermind.Logging;
using Nethermind.Runner.Ethereum.Api;
using Nethermind.Runner.Ethereum.Steps;
using Nethermind.Serialization.Json;
using NUnit.Framework;

namespace Nethermind.Runner.Test.Ethereum.Steps
{
    [TestFixture, Parallelizable(ParallelScope.All)]
    public class EthereumStepsManagerTests
    {
        [Test]
        public async Task When_no_assemblies_defined()
        {
            NethermindApi runnerContext = CreateApi<NethermindApi>();

            IEthereumStepsLoader stepsLoader = new EthereumStepsLoader();
            EthereumStepsManager stepsManager = new EthereumStepsManager(
                stepsLoader,
                runnerContext,
                LimboLogs.Instance);

            using CancellationTokenSource source = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            await stepsManager.InitializeAll(source.Token);
        }

        [Test]
        public async Task With_steps_from_here()
        {
            NethermindApi runnerContext = CreateApi<NethermindApi>();

            IEthereumStepsLoader stepsLoader = new EthereumStepsLoader(GetType().Assembly);
            EthereumStepsManager stepsManager = new EthereumStepsManager(
                stepsLoader,
                runnerContext,
                LimboLogs.Instance);

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
        public async Task With_steps_from_here_AuRa()
        {
            AuRaNethermindApi runnerContext = CreateApi<AuRaNethermindApi>();

            IEthereumStepsLoader stepsLoader = new EthereumStepsLoader(GetType().Assembly);
            EthereumStepsManager stepsManager = new EthereumStepsManager(
                stepsLoader,
                runnerContext,
                LimboLogs.Instance);

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
        public async Task With_failing_steps()
        {
            NethermindApi runnerContext = CreateApi<NethermindApi>();

            IEthereumStepsLoader stepsLoader = new EthereumStepsLoader(GetType().Assembly);
            EthereumStepsManager stepsManager = new EthereumStepsManager(
                stepsLoader,
                runnerContext,
                LimboLogs.Instance);

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
        
        private static T CreateApi<T>() where T : INethermindApi, new() =>
            new T()
            {
                ConfigProvider = new ConfigProvider(),
                EthereumJsonSerializer = new EthereumJsonSerializer(),
                LogManager = LimboLogs.Instance
            };
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
        public StepCAuRa(AuRaNethermindApi runnerContext)
        {
        }

        public override async Task Execute(CancellationToken cancellationToken)
        {
            await Task.Run(() => throw new Exception());
        }
    }

    public class StepCStandard : StepC
    {
        public StepCStandard(NethermindApi runnerContext)
        {
        }
    }
}
