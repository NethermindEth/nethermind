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

using System.Threading.Tasks;
using Nethermind.Consensus.Clique;
using Nethermind.JsonRpc.Modules;
using Nethermind.Runner.Ethereum.Context;

namespace Nethermind.Runner.Ethereum.Steps
{
    public class RegisterRpcModulesClique : RegisterRpcModules
    {
        private readonly CliqueEthereumRunnerContext _context;

        public RegisterRpcModulesClique(CliqueEthereumRunnerContext context) : base(context)
        {
            _context = context;
        }

        public override Task Execute()
        {
            if (_context.BlockTree == null) throw new StepDependencyException(nameof(_context.BlockTree));
            if (_context.SnapshotManager == null) throw new StepDependencyException(nameof(_context.SnapshotManager));
            if (_context.RpcModuleProvider == null) throw new StepDependencyException(nameof(_context.RpcModuleProvider));
            
            Task result = base.Execute();
            if (_context.SnapshotManager == null) throw new StepDependencyException(nameof(_context.SnapshotManager));
            CliqueModule cliqueModule = new CliqueModule(_context.LogManager, new CliqueBridge(_context.BlockProducer as ICliqueBlockProducer, _context.SnapshotManager, _context.BlockTree));
            _context.RpcModuleProvider.Register(new SingletonModulePool<ICliqueModule>(cliqueModule, true));
            return result;
        }
    }
}