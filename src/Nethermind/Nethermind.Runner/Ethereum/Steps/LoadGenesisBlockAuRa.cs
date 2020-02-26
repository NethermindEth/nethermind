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
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Runner.Ethereum.Context;
using Nethermind.Specs.Forks;

namespace Nethermind.Runner.Ethereum.Steps
{
    public class LoadGenesisBlockAuRa : LoadGenesisBlock
    {
        private readonly AuRaEthereumRunnerContext _context;

        public LoadGenesisBlockAuRa(AuRaEthereumRunnerContext context) : base(context)
        {
            _context = context;
        }

        protected override void Load()
        {
            CreateSystemAccounts();
            base.Load();
        }
        
        private void CreateSystemAccounts()
        {
            if (_context.ChainSpec == null) throw new StepDependencyException(nameof(_context.ChainSpec));
            
            bool hasConstructorAllocation = _context.ChainSpec.Allocations.Values.Any(a => a.Constructor != null);
            if (hasConstructorAllocation)
            {
                if (_context.StateProvider == null) throw new StepDependencyException(nameof(_context.StateProvider));
                if (_context.StorageProvider == null) throw new StepDependencyException(nameof(_context.StorageProvider));

                _context.StateProvider.CreateAccount(Address.Zero, UInt256.Zero);
                _context.StorageProvider.Commit();
                _context.StateProvider.Commit(Homestead.Instance);
            }
        }
    }
}