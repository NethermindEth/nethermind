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
using Nethermind.Blockchain.Visitors;
using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Db.Rocks;
using Nethermind.Logging;
using Nethermind.Runner.Ethereum.Api;
using Nethermind.State;

namespace Nethermind.Runner.Ethereum.Steps
{
    /* this code was added as an example for users of extension interfaces */
    
    [RunnerStepDependencies(typeof(ReviewBlockTree))]
    public class RunCustomTools : IStep
    {
        private readonly NethermindApi _api;
        
        public RunCustomTools(NethermindApi api)
        {
            _api = api;
        }
    
        public Task Execute(CancellationToken cancellationToken)
        {
            ILogger logger = _api.LogManager.GetClassLogger();
            IInitConfig initConfig = _api.Config<IInitConfig>();
            
            switch (initConfig.DiagnosticMode)
            {
                case DiagnosticMode.VerifySupply:
                {
                    logger.Info("Genesis supply:");
                    SupplyVerifier supplyVerifier = new SupplyVerifier(logger);
                    StateDb stateDb = new StateDb(_api.DbProvider!.StateDb.Innermost);
                    StateDb codeDb = new StateDb(_api.DbProvider.StateDb.Innermost);
                    StateReader stateReader = new StateReader(stateDb, codeDb, _api.LogManager);
                    stateReader.RunTreeVisitor(supplyVerifier, _api.BlockTree!.Genesis.StateRoot);

                    Block head = _api.BlockTree!.Head;
                    logger.Info($"Head ({head.Number}) block supply:");
                    supplyVerifier = new SupplyVerifier(logger);
                    stateReader.RunTreeVisitor(supplyVerifier, head.StateRoot);
                    break;
                }
                case DiagnosticMode.VerifyRewards:
                    _api.BlockTree!.Accept(new RewardsVerifier(_api.LogManager), cancellationToken);
                    break;
            }
    
            return Task.CompletedTask;
        }
    }
}