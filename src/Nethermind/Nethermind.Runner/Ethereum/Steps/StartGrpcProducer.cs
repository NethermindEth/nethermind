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
using System.Threading.Tasks;
using Nethermind.Grpc;
using Nethermind.Grpc.Producers;
using Nethermind.Runner.Ethereum.Subsystems;

namespace Nethermind.Runner.Ethereum.Steps
{
    [RunnerStepDependency(typeof(InitializeNetwork))]
    public class StartGrpcProducer : IStep, ISubsystemStateAware
    {
        private readonly EthereumRunnerContext _context;

        public StartGrpcProducer(EthereumRunnerContext context)
        {
            _context = context;
            EthereumSubsystemState newState = _context.Config<IGrpcConfig>().Enabled
                ? EthereumSubsystemState.AwaitingInitialization
                : EthereumSubsystemState.Disabled;

            SubsystemStateChanged?.Invoke(this, new SubsystemStateEventArgs(newState));
        }

        public Task Execute()
        {
            IGrpcConfig grpcConfig = _context.Config<IGrpcConfig>();
            if (!grpcConfig.Enabled)
            {
                return Task.CompletedTask;
            }

            SubsystemStateChanged?.Invoke(this, new SubsystemStateEventArgs(EthereumSubsystemState.Initializing));

            if (grpcConfig.ProducerEnabled)
            {
                GrpcProducer grpcProducer = new GrpcProducer(_context.GrpcServer);
                _context.Producers.Add(grpcProducer);
            }

            SubsystemStateChanged?.Invoke(this, new SubsystemStateEventArgs(EthereumSubsystemState.Running));
            return Task.CompletedTask;
        }

        public event EventHandler<SubsystemStateEventArgs> SubsystemStateChanged;

        public EthereumSubsystem MonitoredSubsystem => EthereumSubsystem.Grpc;
    }
}