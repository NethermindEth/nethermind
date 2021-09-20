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

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Consensus;
using Nethermind.Logging;

namespace Nethermind.Merge.Plugin
{
    public class PoSSwitcher : IPoSSwitcher, ITransitionProcessHandler
    {
        private readonly ILogger _logger;
        private UInt256? _terminalTotalDifficulty;
        private Keccak? _terminalBlockHash;
        private long? _firstPoSBlockNumber;

        public PoSSwitcher(ILogManager logManager)
        {
            _logger = logManager.GetClassLogger();
        }
        
        
        public void SetTerminalTotalDifficulty(UInt256 totalDifficulty)
        {
            _terminalTotalDifficulty = totalDifficulty;
        }

        public void SetTerminalPoWHash(Keccak blockHash)
        {
            _terminalBlockHash = blockHash;
        }

        public bool WasEverInPoS()
        {
            return _firstPoSBlockNumber != null;
        }

        public bool IsPos(BlockHeader header, bool trySwitchToPos)
        {
            if (_firstPoSBlockNumber != null && _firstPoSBlockNumber <= header.Number)
            {
                return true;
            }

            if (_terminalBlockHash == null && _terminalTotalDifficulty == null)
            {
                return false;
            }

            if (_firstPoSBlockNumber == null && (_terminalBlockHash == header.ParentHash || header.TotalDifficulty >= _terminalTotalDifficulty))
            {
                if (trySwitchToPos)
                {
                    if (_logger.IsInfo) _logger.Info($"Switched to Proof of Stake at block {header}");
                    _firstPoSBlockNumber = header.Number;
                }

                return true;
            }

            return false;
        }
    }
}
