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

using System.Linq;
using Nethermind.Core;
using Nethermind.Init.Steps;
using Nethermind.Int256;
using Nethermind.Specs.Forks;

namespace Nethermind.Consensus.AuRa.InitializationSteps
{
    public class LoadGenesisBlockAuRa : LoadGenesisBlock
    {
        private readonly AuRaNethermindApi _api;

        public LoadGenesisBlockAuRa(AuRaNethermindApi api) : base(api)
        {
            _api = api;
        }

        protected override void Load()
        {
            CreateSystemAccounts();
            base.Load();
        }
        
        private void CreateSystemAccounts()
        {
            if (_api.ChainSpec == null) throw new StepDependencyException(nameof(_api.ChainSpec));
            
            bool hasConstructorAllocation = _api.ChainSpec.Allocations.Values.Any(a => a.Constructor != null);
            if (hasConstructorAllocation)
            {
                if (_api.StateProvider == null) throw new StepDependencyException(nameof(_api.StateProvider));
                if (_api.StorageProvider == null) throw new StepDependencyException(nameof(_api.StorageProvider));

                _api.StateProvider.CreateAccount(Address.Zero, UInt256.Zero);
                _api.StorageProvider.Commit();
                _api.StateProvider.Commit(Homestead.Instance);
            }
        }
    }
}
