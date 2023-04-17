// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Synchronization;
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
        private readonly ISyncConfig _syncConfig;
        private readonly IDb _metadataDb;
        private readonly IBlockTree _blockTree;
        private readonly ISpecProvider _specProvider;
        private readonly ILogger _logger;
        private Keccak? _terminalBlockHash;

        private long? _terminalBlockNumber;
        private long? _firstPoSBlockNumber;
        private bool _hasEverReachedTerminalDifficulty;
        private Keccak _finalizedBlockHash = Keccak.Zero;
        private bool _terminalBlockExplicitSpecified;
        private UInt256? _finalTotalDifficulty;

        public PoSSwitcher(
            IMergeConfig mergeConfig,
            ISyncConfig syncConfig,
            IDb metadataDb,
            IBlockTree blockTree,
            ISpecProvider specProvider,
            ILogManager logManager)
        {
            _mergeConfig = mergeConfig;
            _syncConfig = syncConfig;
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
            _specProvider.UpdateMergeTransitionInfo(_firstPoSBlockNumber, _mergeConfig.TerminalTotalDifficultyParsed);
            LoadFinalTotalDifficulty();

            if (_terminalBlockNumber is not null || _finalTotalDifficulty is not null)
                _hasEverReachedTerminalDifficulty = true;

            if (_terminalBlockNumber is null)
                _blockTree.NewHeadBlock += CheckIfTerminalBlockReached;

            if (_logger.IsInfo)
                _logger.Info($"Client started with TTD: {TerminalTotalDifficulty}, TTD reached: {_hasEverReachedTerminalDifficulty}, Terminal Block Number {_terminalBlockNumber}, FinalTotalDifficulty: {FinalTotalDifficulty}");
        }

        private void LoadFinalTotalDifficulty()
        {
            _finalTotalDifficulty = _mergeConfig.FinalTotalDifficultyParsed;

            // pivot post TTD, so we know FinalTotalDifficulty
            if (_syncConfig.PivotTotalDifficultyParsed != 0 && TerminalTotalDifficulty is not null && _syncConfig.PivotTotalDifficultyParsed >= TerminalTotalDifficulty)
            {
                _finalTotalDifficulty = _syncConfig.PivotTotalDifficultyParsed;
            }
        }

        private void CheckIfTerminalBlockReached(object? sender, BlockEventArgs e)
        {
            TryUpdateTerminalBlock(e.Block.Header);
        }

        private void LoadFinalizedBlockHash()
        {
            _finalizedBlockHash = LoadHashFromDb(MetadataDbKeys.FinalizedBlockHash) ?? Keccak.Zero;
        }

        public bool TryUpdateTerminalBlock(BlockHeader header)
        {
            if (_terminalBlockExplicitSpecified || TransitionFinished || !header.IsTerminalBlock(_specProvider))
            {
                return false;
            }

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
            else
            {
                if (_logger.IsInfo) _logger.Info($"Updated terminal block {header}");
            }

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
                    if (_logger.IsInfo) _logger.Info($"Reached the first finalized PoS block FinalizedHash: {finalizedHash}, NewHeadHash: {newHeadHash}");
                    _blockTree.NewHeadBlock -= CheckIfTerminalBlockReached;
                }

                _finalizedBlockHash = finalizedHash;
            }
        }

        public bool TransitionFinished => FinalTotalDifficulty is not null || _finalizedBlockHash != Keccak.Zero;

        public (bool IsTerminal, bool IsPostMerge) GetBlockConsensusInfo(BlockHeader header)
        {
            if (_logger.IsTrace)
                _logger.Trace(
                    $"GetBlockConsensusInfo {header.ToString(BlockHeader.Format.FullHashAndNumber)} header.IsPostMerge: {header.IsPostMerge} header.TotalDifficulty {header.TotalDifficulty} header.Difficulty {header.Difficulty} TTD: {_specProvider.TerminalTotalDifficulty} MergeBlockNumber {_specProvider.MergeBlockNumber}, TransitionFinished: {TransitionFinished}");

            bool isTerminal = false, isPostMerge;
            if (_specProvider.TerminalTotalDifficulty is null) // TTD = null, so everything is preMerge
            {
                isTerminal = false;
                isPostMerge = false;
            }
            else if (header.TotalDifficulty is not null && header.TotalDifficulty < _specProvider.TerminalTotalDifficulty) // pre TTD blocks
            {
                // In a hive test, a block is requested from EL with total difficulty < TTD. so IsPostMerge does not work.
                isTerminal = false;
                isPostMerge = false;
            }
            else if (header.IsPostMerge) // block from Engine API, there is no need to check more cases
            {
                isTerminal = false;
                isPostMerge = true;
            }
            else if (header.TotalDifficulty is null || (header.TotalDifficulty == 0 && header.IsGenesis == false)) // we don't know header TD, so we consider header.Difficulty
            {
                isPostMerge = header.Difficulty == 0;
                isTerminal = false; // we can't say if block isTerminal if we don't have TD
            }
            else
            {
                bool theMergeEnabled = (ForkActivation)header.Number >= _specProvider.MergeBlockNumber;
                if (TransitionFinished && theMergeEnabled || _terminalBlockExplicitSpecified && theMergeEnabled) // if transition finished or we know terminalBlock from config we can decide by blockNumber
                {
                    isPostMerge = true;
                }
                else
                {
                    isTerminal = header.IsTerminalBlock(_specProvider); // we're checking if block is terminal if not it should be PostMerge block
                    isPostMerge = !isTerminal;
                }
            }

            header.IsPostMerge = isPostMerge;
            if (_logger.IsTrace)
                _logger.Trace(
                    $"GetBlockConsensusInfo Result: IsTerminal: {isTerminal}, IsPostMerge: {isPostMerge}, {header.ToString(BlockHeader.Format.FullHashAndNumber)} header.IsPostMerge: {header.IsPostMerge} header.TotalDifficulty {header.TotalDifficulty} header.Difficulty {header.Difficulty} TTD: {_specProvider.TerminalTotalDifficulty} MergeBlockNumber {_specProvider.MergeBlockNumber}, TransitionFinished: {TransitionFinished}");
            return (isTerminal, isPostMerge);
        }

        public bool IsPostMerge(BlockHeader header) =>
            GetBlockConsensusInfo(header).IsPostMerge;

        public bool HasEverReachedTerminalBlock() => _hasEverReachedTerminalDifficulty;

        public event EventHandler? TerminalBlockReached;

        public UInt256? TerminalTotalDifficulty => _specProvider.TerminalTotalDifficulty;

        public UInt256? FinalTotalDifficulty => _finalTotalDifficulty;

        public Keccak ConfiguredTerminalBlockHash => _mergeConfig.TerminalBlockHashParsed;

        public long? ConfiguredTerminalBlockNumber => _mergeConfig.TerminalBlockNumber;

        private void LoadTerminalBlock()
        {
            _terminalBlockNumber = _mergeConfig.TerminalBlockNumber ??
                                   _specProvider.MergeBlockNumber?.BlockNumber - 1;

            _terminalBlockExplicitSpecified = _terminalBlockNumber is not null;
            _terminalBlockNumber ??= LoadTerminalBlockNumberFromDb();

            _terminalBlockHash = _mergeConfig.TerminalBlockHashParsed != Keccak.Zero
                ? _mergeConfig.TerminalBlockHashParsed
                : LoadHashFromDb(MetadataDbKeys.TerminalPoWHash);

            if (_terminalBlockNumber is not null)
                _firstPoSBlockNumber = _terminalBlockNumber + 1;
        }

        private long? LoadTerminalBlockNumberFromDb()
        {
            try
            {
                if (_metadataDb.KeyExists(MetadataDbKeys.TerminalPoWNumber))
                {
                    byte[]? hashFromDb = _metadataDb.Get(MetadataDbKeys.TerminalPoWNumber);
                    RlpStream stream = new(hashFromDb!);
                    return stream.DecodeLong();
                }
            }
            catch (RlpException)
            {
                if (_logger.IsWarn) _logger.Warn($"Cannot decode terminal block number");
            }

            return null;
        }

        private Keccak? LoadHashFromDb(int key)
        {
            try
            {
                if (_metadataDb.KeyExists(key))
                {
                    byte[]? hashFromDb = _metadataDb.Get(key);
                    RlpStream stream = new(hashFromDb!);
                    return stream.DecodeKeccak();
                }
            }
            catch (RlpException)
            {
                if (_logger.IsWarn) _logger.Warn($"Cannot decode hash with metadata key: {key}");
            }

            return null;
        }
    }
}
