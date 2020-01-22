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

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.AuRa;
using Nethermind.AuRa.Validators;
using Nethermind.Blockchain;
using Nethermind.Core;

namespace Nethermind.Runner.Ethereum.Steps
{
    [RunnerStepDependency(typeof(LoadGenesisBlock), typeof(LoadChainspec), typeof(InitializeBlockchain))]
    public class InitializeFinalization : IStep
    {
        private readonly EthereumRunnerContext _context;

        public InitializeFinalization(EthereumRunnerContext context)
        {
            _context = context;
        }
        
        public Task Execute()
        {
            _context.FinalizationManager = InitFinalizationManager(_context.AdditionalBlockProcessors);
            return Task.CompletedTask;
        }
        
        private IBlockFinalizationManager InitFinalizationManager(IList<IAdditionalBlockProcessor> blockPreProcessors)
        {
            switch (_context.ChainSpec.SealEngineType)
            {
                case SealEngineType.AuRa:
                    AuRaBlockFinalizationManager finalizationManager = new AuRaBlockFinalizationManager(_context.BlockTree, _context.ChainLevelInfoRepository, _context.BlockProcessor, _context.ValidatorStore, new ValidSealerStrategy(), _context.LogManager);
                    foreach (IAuRaValidator auRaValidator in blockPreProcessors.OfType<IAuRaValidator>())
                    {
                        auRaValidator.SetFinalizationManager(finalizationManager);
                    }

                    return finalizationManager;
                default:
                    return null;
            }
        }
    }
}