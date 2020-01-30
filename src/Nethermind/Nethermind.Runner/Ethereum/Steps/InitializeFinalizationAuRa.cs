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
using Nethermind.Runner.Ethereum.Context;

namespace Nethermind.Runner.Ethereum.Steps
{
    [RunnerStepDependency(typeof(LoadGenesisBlock), typeof(InitializeBlockchain))]
    public class InitializeFinalizationAuRa : IStep
    {
        private readonly AuRaEthereumRunnerContext _context;

        public InitializeFinalizationAuRa(AuRaEthereumRunnerContext context)
        {
            _context = context;
        }
        
        public ValueTask Execute()
        {
            _context.FinalizationManager = InitFinalizationManager(_context.AuRaBlockProcessor);
            return default;
        }
        
        private IBlockFinalizationManager InitFinalizationManager(IAuRaBlockProcessor auRaBlockProcessor)
        {
            AuRaBlockFinalizationManager finalizationManager = new AuRaBlockFinalizationManager(_context.BlockTree, _context.ChainLevelInfoRepository, _context.BlockProcessor, _context.ValidatorStore, new ValidSealerStrategy(), _context.LogManager);
            auRaBlockProcessor.SetFinalizationManager(finalizationManager);
            return finalizationManager;
        }
    }
}