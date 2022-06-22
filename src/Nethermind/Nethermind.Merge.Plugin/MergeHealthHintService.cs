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

using System;
using Nethermind.Blockchain.Services;
using Nethermind.Consensus;

namespace Nethermind.Merge.Plugin
{
    public class MergeHealthHintService : IHealthHintService
    {
        private readonly IHealthHintService _healthHintService;
        private readonly IPoSSwitcher _poSSwitcher;

        public MergeHealthHintService(IHealthHintService? healthHintService, IPoSSwitcher? poSSwitcher)
        {
            _healthHintService = healthHintService ?? throw new ArgumentNullException(nameof(healthHintService));
            _poSSwitcher = poSSwitcher ?? throw new ArgumentNullException(nameof(poSSwitcher));
        }

        public ulong? MaxSecondsIntervalForProcessingBlocksHint()
        {
            if (_poSSwitcher.HasEverReachedTerminalBlock())
            {
                return 12 + 3;
            }

            return _healthHintService.MaxSecondsIntervalForProcessingBlocksHint();
        }

        public ulong? MaxSecondsIntervalForProducingBlocksHint()
        {
            if (_poSSwitcher.HasEverReachedTerminalBlock())
            {
                return long.MaxValue;
            }

            return _healthHintService.MaxSecondsIntervalForProducingBlocksHint();
        }
    }
}
