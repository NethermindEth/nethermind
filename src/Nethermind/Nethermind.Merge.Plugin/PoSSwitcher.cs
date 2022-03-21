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
using Nethermind.Blockchain.Find;
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
        private readonly ILogger _logger;
        private Keccak? _terminalBlockHash;
        private BlockHeader? _firstPoSBlockHeader;

        private long? _terminalBlockNumber;
        private long? _firstPoSBlockNumber;
        private bool _hasEverReachedTerminalDifficulty;
        private Keccak _finalizedBlockHash = Keccak.Zero;
        private bool _terminalBlockExplicitSpecified;

        public PoSSwitcher(
            IMergeConfig mergeConfig,
            IDb metadataDb,
            IBlockTree blockTree,
            ISpecProvider specProvider,
            ILogManager logManager)
        {
            _mergeConfig = mergeConfig;
            _metadataDb = metadataDb;
            _blockTree = blockTree;
            _specProvider = specProvider;
            _logger = logManager.GetClassLogger();

            Initialize();
        }

        private void Initialize()
        {
            LoadTerminalBlock();
            LoadFinalizedBlockHash();

            if (_terminalBlockNumber != null)
                _hasEverReachedTerminalDifficulty = true;
            
            _specProvider.UpdateMergeTransitionInfo(_firstPoSBlockNumber, _mergeConfig.TerminalTotalDifficultyParsed);

            if (_terminalBlockNumber == null)
                _blockTree.NewHeadBlock += CheckIfTerminalBlockReached;

            if (_terminalBlockNumber != null && _finalizedBlockHash != Keccak.Zero)
                _blockTree.NewHeadBlock -= CheckIfTerminalBlockReached;
        }

        private void CheckIfTerminalBlockReached(object? sender, BlockEventArgs e)
        {
            TryUpdateTerminalBlock(e.Block.Header);
        }

        private void LoadFinalizedBlockHash()
        {
            _finalizedBlockHash = LoadHashFromDb(MetadataDbKeys.FinalizedBlockHash) ?? Keccak.Zero;
        }

        // Terminal PoW block: A PoW block that satisfies the following conditions pow_block.total_difficulty >= TERMINAL_TOTAL_DIFFICULTY and pow_block.parent_block.total_difficulty < TERMINAL_TOTAL_DIFFICULTY
        // https://github.com/ethereum/EIPs/blob/d896145678bd65d3eafd8749690c1b5228875c39/EIPS/eip-3675.md#specification
        public bool IsTerminalBlock(BlockHeader header, BlockHeader? parent = null)
        {
            bool isTerminalBlock = false;
            bool ttdRequirement = header.TotalDifficulty >= TerminalTotalDifficulty;
            if (ttdRequirement && header.IsGenesis)
                return true;
            
            if (ttdRequirement && header.IsPostMerge == false)
            {
                if (parent == null)
                {
                    parent = _blockTree.FindParentHeader(header!, BlockTreeLookupOptions.None);
                }

                if (parent != null && parent.TotalDifficulty < TerminalTotalDifficulty)
                {
                    isTerminalBlock = true;
                }
            }

            return isTerminalBlock;
        }
        
        public bool TryUpdateTerminalBlock(BlockHeader header, BlockHeader? parent = null)
        {
            if (_terminalBlockExplicitSpecified || TransitionFinished || IsTerminalBlock(header, parent) == false)
                return false;

            _terminalBlockNumber = header.Number;
            _terminalBlockHash = header.Hash;
            _metadataDb.Set(MetadataDbKeys.TerminalPoWNumber, Rlp.Encode(_terminalBlockNumber.Value).Bytes);
            _metadataDb.Set(MetadataDbKeys.TerminalPoWHash, Rlp.Encode(_terminalBlockHash).Bytes);
            _firstPoSBlockNumber = header.Number + 1;
            _specProvider.UpdateMergeTransitionInfo(_firstPoSBlockNumber.Value);

            if (_hasEverReachedTerminalDifficulty == false)
            {
                TerminalBlockReached?.Invoke(this, EventArgs.Empty);
                _hasEverReachedTerminalDifficulty = true;
                if (_logger.IsInfo) _logger.Info($"Reached terminal block {header}");
            }
            else if (_logger.IsInfo) _logger.Info($"Updated terminal block {header}");

            return true;
        }

        public void ForkchoiceUpdated(BlockHeader newHeadHash, Keccak finalizedHash)
        {
            if (finalizedHash != Keccak.Zero && _finalizedBlockHash == Keccak.Zero)
            {
                _blockTree.NewHeadBlock -= CheckIfTerminalBlockReached;
            }

            if (finalizedHash != Keccak.Zero)
            {
                if (_finalizedBlockHash == Keccak.Zero)
                {
                    if (_logger.IsInfo)
                        _logger.Info(
                            $"Reached the first finalized PoS block FinalizedHash: {finalizedHash}, NewHeadHash: {newHeadHash}");
                    _blockTree.NewHeadBlock -= CheckIfTerminalBlockReached;
                }

                _finalizedBlockHash = finalizedHash;
                // ToDo need to discuss with Sarah, this should be moved to BlockTree or FinalizationManager
                _metadataDb.Set(MetadataDbKeys.FinalizedBlockHash, Rlp.Encode(_finalizedBlockHash).Bytes);
            }

            if (_firstPoSBlockHeader == null)
            {
                if (_logger.IsInfo) _logger.Info($"Received the first forkchoiceUpdated at block {newHeadHash}");
                _firstPoSBlockHeader = newHeadHash;
                _metadataDb.Set(MetadataDbKeys.FirstPoSHash, Rlp.Encode(_firstPoSBlockHeader.Hash).Bytes);
            }
        }

        public bool TransitionFinished => _finalizedBlockHash != Keccak.Zero;

        public (bool IsTerminal, bool IsPostMerge) GetBlockSwitchInfo(BlockHeader header, BlockHeader? parent = null)
        {
            if (header.IsPostMerge)
                return (false, true);
            if (_specProvider.TerminalTotalDifficulty == null)
                return (false, false);

            bool isTerminal = false, isPostMerge = false;

            if (header.TotalDifficulty == null)
            {
                return (false, header.Difficulty == 0);
            }
            
            if (header.TotalDifficulty < _specProvider.TerminalTotalDifficulty)
                return (false, false);

            bool theMergeEnabled = header.Number >= _specProvider.MergeBlockNumber;
            if (TransitionFinished && theMergeEnabled || _terminalBlockExplicitSpecified && theMergeEnabled)
            {
                isPostMerge = true;
            }
            else
            {
                isTerminal = IsTerminalBlock(header, parent);
                isPostMerge = !isTerminal;
            }

            header.IsPostMerge = isPostMerge;
            return (isTerminal, isPostMerge);
        }

        public bool IsPostMerge(BlockHeader header, BlockHeader? parent = null) =>
            GetBlockSwitchInfo(header, parent).IsPostMerge;

        public bool HasEverReachedTerminalBlock() => _hasEverReachedTerminalDifficulty;

        public event EventHandler? TerminalBlockReached;

        public UInt256? TerminalTotalDifficulty => _specProvider.TerminalTotalDifficulty;
        public long? TerminalBlockNumber => _terminalBlockNumber;

        public Keccak? TerminalBlockHash => _terminalBlockHash;
        
        public Keccak? ConfiguredTerminalBlockHash => _mergeConfig.TerminalBlockHash;
        
        public long? ConfiguredTerminalBlockNumber => _mergeConfig.TerminalBlockNumber ??
                                                        _specProvider.MergeBlockNumber - 1;

        private void LoadTerminalBlock()
        {
            _terminalBlockNumber = ConfiguredTerminalBlockNumber;

            _terminalBlockExplicitSpecified = _terminalBlockNumber != null;
            _terminalBlockNumber ??= LoadTerminalBlockNumberFromDb();

            _terminalBlockHash = _mergeConfig.TerminalBlockHash != Keccak.Zero
                ? _mergeConfig.TerminalBlockHash
                : LoadHashFromDb(MetadataDbKeys.TerminalPoWHash);

            if (_terminalBlockNumber != null)
                _firstPoSBlockNumber = _terminalBlockNumber + 1;
        }

        private long? LoadTerminalBlockNumberFromDb()
        {
            if (_metadataDb.KeyExists(MetadataDbKeys.TerminalPoWNumber))
            {
                byte[]? hashFromDb = _metadataDb.Get(MetadataDbKeys.TerminalPoWNumber);
                RlpStream stream = new(hashFromDb!);
                return stream.DecodeLong();
            }

            return null;
        }

        private Keccak? LoadHashFromDb(int key)
        {
            if (_metadataDb.KeyExists(key))
            {
                byte[]? hashFromDb = _metadataDb.Get(key);
                RlpStream stream = new(hashFromDb!);
                return stream.DecodeKeccak();
            }

            return null;
        }
    }
}
