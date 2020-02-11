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

using System.Linq;
using System.Threading.Tasks;
using Nethermind.PubSub;
using Nethermind.Runner.Ethereum.Context;

namespace Nethermind.Runner.Ethereum.Steps
{
    [RunnerStepDependencies(typeof(InitializeBlockchain), typeof(StartGrpcProducer), typeof(StartKafkaProducer))]
    public class AddSubscription : IStep
    {
        private readonly EthereumRunnerContext _context;

        public AddSubscription(EthereumRunnerContext context)
        {
            _context = context;
        }

        public Task Execute()
        {
            ISubscription subscription = _context.Producers.Any() 
                ? new Subscription(_context.Producers, _context.MainBlockProcessor, _context.LogManager) 
                : (ISubscription) new EmptySubscription();

            _context.DisposeStack.Push(subscription);

            return Task.CompletedTask;
        }
    }
}