//  Copyright (c) 2018 Demerzel Solutions Limited
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
using Nethermind.Config;
using Nethermind.Logging;
using Nethermind.Runner.Ethereum.Context;
using Nethermind.Runner.Ethereum.Steps;
using NUnit.Framework;

namespace Nethermind.Runner.Test.Ethereum.Steps
{
    [TestFixture]
    public class EthereumStepsManagerTests
    {
        [Test]
        public void When_no_assemblies_defined()
        {
            EthereumRunnerContext runnerContext = new EthereumRunnerContext(
                new ConfigProvider(),
                LimboLogs.Instance);

            IEthereumStepsLoader stepsLoader = new EthereumStepsLoader();
            EthereumStepsManager stepsManager = new EthereumStepsManager(
                stepsLoader,
                runnerContext,
                LimboLogs.Instance);
            
            CancellationTokenSource source = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            stepsManager.InitializeAll(source.Token);
        }
        
        [Test]
        public void With_steps_from_here()
        {
            EthereumRunnerContext runnerContext = new EthereumRunnerContext(
                new ConfigProvider(),
                LimboLogs.Instance);

            IEthereumStepsLoader stepsLoader = new EthereumStepsLoader(GetType().Assembly);
            EthereumStepsManager stepsManager = new EthereumStepsManager(
                stepsLoader,
                runnerContext,
                LimboLogs.Instance);
            
            CancellationTokenSource source = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            stepsManager.InitializeAll(source.Token);
        }
        
        [Test]
        public void With_steps_from_here_Clique()
        {
            EthereumRunnerContext runnerContext = new CliqueEthereumRunnerContext(
                new ConfigProvider(),
                LimboLogs.Instance);

            IEthereumStepsLoader stepsLoader = new EthereumStepsLoader(GetType().Assembly);
            EthereumStepsManager stepsManager = new EthereumStepsManager(
                stepsLoader,
                runnerContext,
                LimboLogs.Instance);
            
            CancellationTokenSource source = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            stepsManager.InitializeAll(source.Token);
        }
    }
    
    public class StepA : IStep
    {
        public Task Execute(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public StepA(EthereumRunnerContext runnerContext)
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
        
        public StepB(EthereumRunnerContext runnerContext)
        {
        }
    }
    
    public abstract class StepC : IStep
    {
        public Task Execute(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }
    
    public class StepCAuRa : StepC
    {
        public StepCAuRa(AuRaEthereumRunnerContext runnerContext)
        {
        }
    }
    
    public class StepCClique : StepC
    {
        public StepCClique(CliqueEthereumRunnerContext runnerContext)
        {
        }
    }
    
    public class StepCStandard : StepC
    {
        public StepCStandard(EthereumRunnerContext runnerContext)
        {
        }
    }
}