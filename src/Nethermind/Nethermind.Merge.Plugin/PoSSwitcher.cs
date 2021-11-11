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
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Consensus;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Handlers;

namespace Nethermind.Merge.Plugin
{
    // ToDo maybe we should persist data (_terminalTotalDifficulty, _terminalBlockHash, _firstPoSBlockHeader, _terminalPoWBlockNumber)
    public class PoSSwitcher : IPoSSwitcher, ITransitionProcessHandler
    {
        private readonly IMergeConfig _mergeConfig;
        private readonly IDb _db;
        private readonly IBlockTree _blockTree;
        private readonly ILogger _logger;
        private UInt256? _terminalTotalDifficulty;
        private Keccak? _terminalBlockHash;
        private BlockHeader? _firstPoSBlockHeader;
        private long? _terminalPoWBlockNumber;

        public PoSSwitcher(ILogManager logManager, IMergeConfig mergeConfig, IDb db, IBlockTree blockTree)
        {
            _mergeConfig = mergeConfig;
            _db = db;
            _blockTree = blockTree;
            _terminalTotalDifficulty = LoadTerminalTotalDifficulty();
            _logger = logManager.GetClassLogger();

            if (_terminalPoWBlockNumber == null)
            {
                _blockTree.NewHeadBlock += CheckIfTerminalPoWBlockReached;
            }
        }

        private void CheckIfTerminalPoWBlockReached(object? sender, BlockEventArgs e)
        {
            if (_terminalBlockHash == e.Block.Hash || e.Block.TotalDifficulty >= _terminalTotalDifficulty)
            {
                _terminalPoWBlockNumber = e.Block.Number;
                _blockTree.NewHeadBlock -= CheckIfTerminalPoWBlockReached;
                if (_logger.IsInfo) _logger.Info($"Reached terminal PoW block {e.Block}");
            }
        }

        public UInt256? TerminalTotalDifficulty
        {
            get => _terminalTotalDifficulty;
            set => SetTerminalTotalDifficulty(value);
        }

        private UInt256? LoadTerminalTotalDifficulty()
        {
            return _mergeConfig.TerminalTotalDifficulty; 
            // ToDo we need to implement it to have persistance
            //    ?? (_db.KeyExists(MetadataDbKeys.TerminalTotalDifficulty) ? Rlp.Decode<UInt256?>(_db.Get(MetadataDbKeys.TerminalTotalDifficulty)) : null);
        }
        
        private void SetTerminalTotalDifficulty(UInt256? totalDifficulty)
        {
            _terminalTotalDifficulty = totalDifficulty;
            // ToDo we need to implement it to have persistance
            // _db.Set(MetadataDbKeys.TerminalTotalDifficulty, Rlp.Encode(_terminalTotalDifficulty).Bytes);
        }

        public void SetTerminalPoWHash(Keccak blockHash)
        {
            _terminalBlockHash = blockHash;
        }
        
        public void ForkchoiceUpdated(BlockHeader header)
        {
            if (_firstPoSBlockHeader == null)
            {
                if (_logger.IsInfo) _logger.Info($"Received the first forkchoiceUpdated at block {header}");
                _firstPoSBlockHeader = header;
            }
        }

        public bool IsPos(BlockHeader header)
        {
            return header.IsPostMerge ||
                   (_firstPoSBlockHeader != null && header.Number >= _firstPoSBlockHeader.Number);
        }

        public bool HasEverReachedTerminalPoWBlock()
        {
            return _terminalPoWBlockNumber != null;
        }

        public event EventHandler? TerminalPoWBlockReached;
    }
}
