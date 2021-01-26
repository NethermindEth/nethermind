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

using Nethermind.Core;
using Nethermind.Crypto;

namespace Nethermind.Consensus.Clique
{
    /// <summary>
    /// This small thing caused so much trouble that it deserves its own class
    /// </summary>
    internal class WiggleRandomizer
    {
        private readonly ICryptoRandom _cryptoRandom;
        private readonly ISnapshotManager _snapshotManager;

        private long _lastWiggleAtNumber;
        
        private int _lastWiggle;
        
        public WiggleRandomizer(ICryptoRandom cryptoRandom, ISnapshotManager snapshotManager)
        {
            _cryptoRandom = cryptoRandom;
            _snapshotManager = snapshotManager;
        }

        public int WiggleFor(BlockHeader header)
        {
            if (header.Difficulty == Clique.DifficultyInTurn)
            {
                return 0;
            }
            
            if (header.Number != _lastWiggleAtNumber)
            {
                int multiplier = _snapshotManager.GetOrCreateSnapshot(header.Number - 1, header.ParentHash!).Signers.Count / 2 + 1;
                int randomPart = _cryptoRandom.NextInt(multiplier * Clique.WiggleTime);
                _lastWiggle = randomPart;
                _lastWiggleAtNumber = header.Number;
            }

            return _lastWiggle;
        }
    }
}
