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
// 

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.State;

namespace Nethermind.Consensus.AuRa;

public class ContractRewriter
{
    private readonly IDictionary<long, IDictionary<Address, byte[]>> _contractOverrides;

    public ContractRewriter(IDictionary<long, IDictionary<Address, byte[]>> contractOverrides)
    {
        _contractOverrides = contractOverrides;
    }

    public void RewriteContracts(long blockNumber, IStateProvider stateProvider, IReleaseSpec spec)
    {
        if (_contractOverrides.TryGetValue(blockNumber, out IDictionary<Address, byte[]> overrides))
        {
            foreach (KeyValuePair<Address, byte[]> contractOverride in overrides)
            {
                Keccak codeHash = stateProvider.UpdateCode(contractOverride.Value);
                stateProvider.UpdateCodeHash(contractOverride.Key, codeHash, spec);
            }
        }
    }
}
