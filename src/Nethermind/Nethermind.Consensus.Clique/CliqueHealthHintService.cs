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
// 

using System;
using Nethermind.Blockchain;
using Nethermind.Specs.ChainSpecStyle;

namespace Nethermind.Consensus.Clique
{
    public class CliqueHealthHintService : IHealthHintService
    {
        private readonly ISnapshotManager _snapshotManager;
        private readonly ChainSpec _chainSpec;
        
        public CliqueHealthHintService(ISnapshotManager snapshotManager, ChainSpec chainSpec)
        {
            _snapshotManager = snapshotManager;
            _chainSpec = chainSpec;
        }
        public ulong? MaxSecondsIntervalForProcessingBlocksHint()
        {
            return _chainSpec.Clique.Period * HealthHintConstants.ProcessingSafetyMultiplier;
        }

        public ulong? MaxSecondsIntervalForProducingBlocksHint()
        {
            return Math.Max(_snapshotManager.GetLastSignersCount(), 1) * _chainSpec.Clique.Period *
                HealthHintConstants.ProducingSafetyMultiplier;
        }
    }
}
