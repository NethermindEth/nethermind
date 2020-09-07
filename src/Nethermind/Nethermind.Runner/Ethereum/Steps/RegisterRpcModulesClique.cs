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

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Consensus.Clique;
using Nethermind.JsonRpc.Modules;
using Nethermind.Runner.Ethereum.Api;

namespace Nethermind.Runner.Ethereum.Steps
{
    public class RegisterRpcModulesClique : RegisterRpcModules
    {
        private readonly CliqueNethermindApi _api;

        public RegisterRpcModulesClique(CliqueNethermindApi api) : base(api)
        {
            _api = api;
        }

        public override Task Execute(CancellationToken cancellationToken)
        {
            if (_api.BlockTree == null) throw new StepDependencyException(nameof(_api.BlockTree));
            if (_api.SnapshotManager == null) throw new StepDependencyException(nameof(_api.SnapshotManager));
            if (_api.RpcModuleProvider == null) throw new StepDependencyException(nameof(_api.RpcModuleProvider));
            
            Task result = base.Execute(cancellationToken);
            if (_api.SnapshotManager == null) throw new StepDependencyException(nameof(_api.SnapshotManager));
            CliqueModule cliqueModule = new CliqueModule(_api.LogManager, new CliqueBridge(_api.BlockProducer as ICliqueBlockProducer, _api.SnapshotManager, _api.BlockTree));
            _api.RpcModuleProvider.Register(new SingletonModulePool<ICliqueModule>(cliqueModule, true));
            return result;
        }
    }
}