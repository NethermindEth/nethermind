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
using Nethermind.Serialization.Rlp;
using Nethermind.Specs.ChainSpecStyle;

namespace Nethermind.Merge.Plugin
{

    /*
      The class is responsible for all logic required to switch to PoS consensus.
      More details: https://eips.ethereum.org/EIPS/eip-3675
      
      We divided our transition process into three steps:
      1) We reached TTD with PoWs blocks
      2) We received the first forkchoiceUpdated
      3) We finalized the first PoS block
      
      The important parameters for the transition process: 
      TERMINAL_TOTAL_DIFFICULTY, FORK_NEXT_VALUE, TERMINAL_BLOCK_HASH, TERMINAL_BLOCK_NUMBER.
      
      We have different sources of these parameters. The above list starts from the highest priority:
      1) MergeConfig - we should be able to override every parameter with CLI arguments
      2) ChainSpec - we can specify our parameters during the release. Moreover, it allows us to migrate to geth chainspec in future
      3) Memory/Database - needed for the dynamic process of transition. We won't know a terminal block number before the merge
     */
    
    public class PoSSwitcher : IPoSSwitcher
    {
        private readonly IMergeConfig _mergeConfig;
        private readonly IDb _metadataDb;
        private readonly IBlockTree _blockTree;
        private readonly ISpecProvider _specProvider;
        private readonly ChainSpec _chainSpec;
        private readonly ILogger _logger;
        private UInt256? _terminalTotalDifficulty;
        private Keccak? _terminalBlockHash;
        private BlockHeader? _firstPoSBlockHeader;

        private long? _terminalPoWBlockNumber;
        private long? _firstPoSBlockNumber;
        private bool _hasEverReachedTerminalDifficulty;
        private Keccak _finalizedBlockHash = Keccak.Zero;

        public PoSSwitcher(
            IMergeConfig mergeConfig,
            IDb metadataDb,
            IBlockTree blockTree, 
            ISpecProvider specProvider,
            ChainSpec chainSpec,
            ILogManager logManager)
        {
            _mergeConfig = mergeConfig;
            _metadataDb = metadataDb;
            _blockTree = blockTree;
            _specProvider = specProvider;
            _chainSpec = chainSpec;
            _logger = logManager.GetClassLogger();
            
            Initialize();
        }

        private void Initialize()
        {
            LoadTerminalTotalDifficulty();
            LoadTerminalPoWBlock();
            LoadFinalizedBlockHash();
            
            if (_terminalPoWBlockNumber != null)
                _hasEverReachedTerminalDifficulty = true;
            if (_firstPoSBlockNumber != null && _specProvider.MergeBlockNumber != _firstPoSBlockNumber)
                _specProvider.UpdateMergeTransitionInfo(_firstPoSBlockNumber.Value);
            
            if (_terminalPoWBlockNumber == null)
                _blockTree.NewHeadBlock += CheckIfTerminalPoWBlockReached;

            if (_terminalPoWBlockNumber != null && _finalizedBlockHash != Keccak.Zero)
                _blockTree.NewHeadBlock -= CheckIfTerminalPoWBlockReached;
        }
        
        private void CheckIfTerminalPoWBlockReached(object? sender, BlockEventArgs e)
        {
            if (_terminalBlockHash == e.Block.Hash || (e.Block.TotalDifficulty >= _terminalTotalDifficulty))
            {
                UpdateTerminalBlock(e.Block.Header);
            }
        }

        private void LoadTerminalTotalDifficulty()
        {
            _terminalTotalDifficulty = _mergeConfig.TerminalTotalDifficultyParsed ??
                                       _chainSpec.TerminalTotalDifficulty;
        }

        private void LoadFinalizedBlockHash()
        {
            _finalizedBlockHash = LoadHashFromDb(MetadataDbKeys.FinalizedBlockHash) ?? Keccak.Zero;
        }
        
        public void UpdateTerminalBlock(BlockHeader blockHeader)
        {
            if (blockHeader.Difficulty != 0) // PostMerge blocks have Difficulty == 0. We are interested here in Terminal PoW block
            {
                _terminalPoWBlockNumber = blockHeader.Number;
                _terminalBlockHash = blockHeader.Hash;
                _metadataDb.Set(MetadataDbKeys.TerminalPoWNumber, Rlp.Encode(_terminalPoWBlockNumber.Value).Bytes);
                _metadataDb.Set(MetadataDbKeys.TerminalPoWHash, Rlp.Encode(_terminalBlockHash).Bytes);
                _firstPoSBlockNumber = blockHeader.Number + 1;
                _specProvider.UpdateMergeTransitionInfo(_firstPoSBlockNumber.Value);
            }

            if (_hasEverReachedTerminalDifficulty == false)
            {
                TerminalPoWBlockReached?.Invoke(this, EventArgs.Empty);
                _hasEverReachedTerminalDifficulty = true;
            }
                
            // ToDo
            _blockTree.NewHeadBlock -= CheckIfTerminalPoWBlockReached;
            if (_logger.IsInfo) _logger.Info($"Reached terminal PoW block {blockHeader}");
            
        }

        public void ForkchoiceUpdated(BlockHeader newHeadHash, Keccak finalizedHash)
        {
            if (finalizedHash != Keccak.Zero && _finalizedBlockHash == Keccak.Zero)
            {
                _blockTree.NewHeadBlock -= CheckIfTerminalPoWBlockReached;
            }
            
            if (finalizedHash != Keccak.Zero)
            {
                if (_finalizedBlockHash == Keccak.Zero)
                {
                    if (_logger.IsInfo) _logger.Info($"Reached the first finalized PoS block FinalizedHash: {finalizedHash}, NewHeadHash: {newHeadHash}");
                    _blockTree.NewHeadBlock -= CheckIfTerminalPoWBlockReached;
                }

                _finalizedBlockHash = finalizedHash;
                // ToDo need to discuss with Sarah, this method should be moved to BlockTree or FinalizationManager
                _metadataDb.Set(MetadataDbKeys.FinalizedBlockHash, Rlp.Encode(_finalizedBlockHash).Bytes);
            }

            if (_firstPoSBlockHeader == null)
            {
                if (_logger.IsInfo) _logger.Info($"Received the first forkchoiceUpdated at block {newHeadHash}");
                _firstPoSBlockHeader = newHeadHash;
                _metadataDb.Set(MetadataDbKeys.FirstPoSHash, Rlp.Encode(_firstPoSBlockHeader.Hash).Bytes);
            }
        }

        public bool IsPos(BlockHeader header)
        {
            return header.IsPostMerge || header.Number > _terminalPoWBlockNumber ||
                (_firstPoSBlockHeader != null && header.Number >= _firstPoSBlockHeader.Number); // ToDo need to think more about the last case, probably we will remove it
        }

        public bool HasEverReachedTerminalPoWBlock() => _hasEverReachedTerminalDifficulty;

        public event EventHandler? TerminalPoWBlockReached;

        public UInt256? TerminalTotalDifficulty => _terminalTotalDifficulty;

        private void LoadTerminalPoWBlock()
        {
            _terminalPoWBlockNumber = _mergeConfig.TerminalBlockNumber ??
                                      _specProvider.MergeBlockNumber - 1 ??
                                      LoadPoWBlockNumberFromDb();
            
            _terminalBlockHash = _mergeConfig.TerminalBlockHash != Keccak.Zero 
                                    ? _mergeConfig.TerminalBlockHash 
                                    : LoadHashFromDb(MetadataDbKeys.TerminalPoWHash);

            if (_terminalPoWBlockNumber != null)
                _firstPoSBlockNumber = _terminalPoWBlockNumber + 1;
        }
        
        private long? LoadPoWBlockNumberFromDb()
        {
            if (_metadataDb.KeyExists(MetadataDbKeys.TerminalPoWNumber))
            {
                byte[]? hashFromDb = _metadataDb.Get(MetadataDbKeys.TerminalPoWNumber);
                RlpStream stream = new (hashFromDb!);
                return stream.DecodeLong();   
            }
            
            return null;
        }

        private Keccak? LoadHashFromDb(int key)
        {
            if (_metadataDb.KeyExists(key))
            {
                byte[]? hashFromDb = _metadataDb.Get(key);
                RlpStream stream = new (hashFromDb!);
                return stream.DecodeKeccak();   
            }
            
            return null;
        }
    }
}
