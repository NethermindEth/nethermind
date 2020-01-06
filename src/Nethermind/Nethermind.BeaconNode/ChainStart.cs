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
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Nethermind.Core2.Containers;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Types;
using Nethermind.Logging.Microsoft;

namespace Nethermind.BeaconNode
{
    public class ChainStart
    {
        private readonly ForkChoice _forkChoice;
        private readonly Genesis _genesis;
        private readonly ILogger _logger;

        public ChainStart(ILogger<ChainStart> logger,
            Genesis genesis,
            ForkChoice forkChoice)
        {
            _logger = logger;
            _genesis = genesis;
            _forkChoice = forkChoice;
        }

        public async Task<bool> TryGenesisAsync(Hash32 eth1BlockHash, ulong eth1Timestamp, IList<Deposit> deposits)
        {
            return await Task.Run(() =>
            {
                if (_logger.IsDebug()) LogDebug.TryGenesis(_logger, eth1BlockHash, eth1Timestamp, deposits.Count, null);

                BeaconState candidateState = _genesis.InitializeBeaconStateFromEth1(eth1BlockHash, eth1Timestamp, deposits);
                if (_genesis.IsValidGenesisState(candidateState))
                {
                    BeaconState genesisState = candidateState;
                    _ = _forkChoice.GetGenesisStore(genesisState);
                    return true;
                }
                return false;
            });
        }
    }
}
