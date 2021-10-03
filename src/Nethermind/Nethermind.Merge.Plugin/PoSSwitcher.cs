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
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Handlers;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Merge.Plugin
{
    // ToDo think about reorgs in this class & maybe we should persist data (_terminalTotalDifficulty, _terminalBlockHash, _firstPoSBlockHeader)  to db
    public class PoSSwitcher : IPoSSwitcher, ITransitionProcessHandler
    {
        private readonly IMergeConfig _mergeConfig;
        private readonly IDb _db;
        private readonly ILogger _logger;
        private UInt256? _terminalTotalDifficulty;
        private Keccak? _terminalBlockHash;
        private BlockHeader? _firstPoSBlockHeader;

        public PoSSwitcher(ILogManager logManager, IMergeConfig mergeConfig, IDb db)
        {
            _mergeConfig = mergeConfig;
            _db = db;
            _terminalTotalDifficulty = LoadTerminalTotalDifficulty();
            _logger = logManager.GetClassLogger();
        }

        public UInt256? TerminalTotalDifficulty
        {
            get => _terminalTotalDifficulty;
            set => SetTerminalTotalDifficulty(value);
        }

        private UInt256? LoadTerminalTotalDifficulty()
        {
            return _mergeConfig.TerminalTotalDifficulty ??
                   (_db.KeyExists(MetadataDbKeys.TerminalTotalDifficulty) ? Rlp.Decode<UInt256?>(_db.Get(MetadataDbKeys.TerminalTotalDifficulty))
                       : null);
        }
        
        private void SetTerminalTotalDifficulty(UInt256? totalDifficulty)
        {
            _terminalTotalDifficulty = totalDifficulty;
            _db.Set(MetadataDbKeys.TerminalTotalDifficulty, Rlp.Encode(_terminalTotalDifficulty).Bytes);
        }

        public void SetTerminalPoWHash(Keccak blockHash)
        {
            _terminalBlockHash = blockHash;
        }

        public bool TrySwitchToPos(BlockHeader header)
        {
            return VerifyPoS(header, true);
        }

        public bool IsPos(BlockHeader header)
        {
            return VerifyPoS(header, false);
        }

        public bool HasEverBeenInPos()
        {
            return _firstPoSBlockHeader != null;
        }

        private bool VerifyPoS(BlockHeader header, bool withSwitchToPoS)
        {
            if (_firstPoSBlockHeader != null && _firstPoSBlockHeader.TotalDifficulty <= header.TotalDifficulty)
            {
                return true;
            }

            if (_terminalBlockHash == null && _terminalTotalDifficulty == null)
            {
                return false;
            }

            if (_firstPoSBlockHeader == null && (_terminalBlockHash == header.ParentHash || header.TotalDifficulty >= _terminalTotalDifficulty))
            {
                if (withSwitchToPoS)
                {
                    if (_logger.IsInfo) _logger.Info($"Switched to Proof of Stake at block {header}");
                    _firstPoSBlockHeader = header;
                }

                return true;
            }

            return false;
        }
    }
}
