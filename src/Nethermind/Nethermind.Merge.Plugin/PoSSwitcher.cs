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
using Nethermind.Core.Specs;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Handlers;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Merge.Plugin
{
    // ToDo maybe we should persist data (_terminalTotalDifficulty, _terminalBlockHash, _firstPoSBlockHeader, _terminalPoWBlockNumber)
    public class PoSSwitcher : IPoSSwitcher, ITransitionProcessHandler
    {
        private readonly IMergeConfig _mergeConfig;
        private readonly IDb _db;
        private readonly IBlockTree _blockTree;
        private readonly ISpecProvider _specProvider;
        private readonly ILogger _logger;
        private UInt256? _terminalTotalDifficulty;
        private Keccak? _terminalBlockHash;
        private BlockHeader? _firstPoSBlockHeader;
        private long? _terminalPoWBlockNumber;

        public PoSSwitcher(ILogManager logManager, IMergeConfig mergeConfig, IDb db, IBlockTree blockTree, ISpecProvider specProvider)
        {
            _mergeConfig = mergeConfig;
            _db = db;
            _blockTree = blockTree;
            _specProvider = specProvider;
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
                _specProvider.UpdateMergeBlockInfo(e.Block.Number + 1);
                TerminalPoWBlockReached?.Invoke(this, EventArgs.Empty);
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
            if (_db.KeyExists(MetadataDbKeys.TerminalTotalDifficulty))
            {
                byte[]? difficultyFromDb = _db.Get(MetadataDbKeys.TerminalTotalDifficulty);
                RlpStream stream = new RlpStream(difficultyFromDb!);
                return stream.DecodeUInt256();
            }

            return _mergeConfig.TerminalTotalDifficulty;
        }

        private void SetTerminalTotalDifficulty(UInt256? totalDifficulty)
        {
            _terminalTotalDifficulty = totalDifficulty;
            _db.Set(MetadataDbKeys.TerminalTotalDifficulty, Rlp.Encode(_terminalTotalDifficulty).Bytes);
        }

        public void SetTerminalPoWHash(Keccak blockHash)
        {
            _terminalBlockHash = blockHash;
            _db.Set(MetadataDbKeys.TerminalPoWHash, Rlp.Encode(_terminalBlockHash).Bytes);
        }

        public void ForkchoiceUpdated(BlockHeader newBlockHeader, BlockHeader finalizedHeader)
        {
            if (_firstPoSBlockHeader == null)
            {
                if (_logger.IsInfo) _logger.Info($"Received the first forkchoiceUpdated at block {newBlockHeader}");
                _firstPoSBlockHeader = newBlockHeader;
                _db.Set(MetadataDbKeys.FirstPoSBlockHash, Rlp.Encode(_firstPoSBlockHeader.Hash).Bytes);
                _db.Set(MetadataDbKeys.FinalizedBlockHash, Rlp.Encode(finalizedHeader.Hash).Bytes);
            }
        }

        public bool IsPos(BlockHeader header)
        {
            return header.IsPostMerge ||
                   (_firstPoSBlockHeader != null && header.Number >= _firstPoSBlockHeader.Number) || header.Number > _terminalPoWBlockNumber;
        }

        public bool HasEverReachedTerminalPoWBlock()
        {
            return _terminalPoWBlockNumber != null;
        }

        public event EventHandler? TerminalPoWBlockReached;
    }
}
