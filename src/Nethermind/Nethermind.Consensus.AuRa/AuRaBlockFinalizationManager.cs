// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Consensus.AuRa.Validators;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.State.Repositories;
using Nito.Collections;

namespace Nethermind.Consensus.AuRa
{
    public class AuRaBlockFinalizationManager : IAuRaBlockFinalizationManager
    {
        private static readonly List<BlockHeader> Empty = new List<BlockHeader>();
        private readonly IBlockTree _blockTree;
        private readonly IChainLevelInfoRepository _chainLevelInfoRepository;
        private readonly ILogger _logger;
        private readonly IBlockProcessor _blockProcessor;
        private readonly IValidatorStore _validatorStore;
        private readonly IValidSealerStrategy _validSealerStrategy;
        private readonly long _twoThirdsMajorityTransition;
        private long _lastFinalizedBlockLevel;
        private Keccak _lastProcessedBlockHash = Keccak.EmptyTreeHash;
        private readonly ValidationStampCollection _consecutiveValidatorsForNotYetFinalizedBlocks = new ValidationStampCollection();

        public AuRaBlockFinalizationManager(
            IBlockTree blockTree,
            IChainLevelInfoRepository chainLevelInfoRepository,
            IBlockProcessor blockProcessor,
            IValidatorStore validatorStore,
            IValidSealerStrategy validSealerStrategy,
            ILogManager logManager,
            long twoThirdsMajorityTransition = long.MaxValue)
        {
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _chainLevelInfoRepository = chainLevelInfoRepository ?? throw new ArgumentNullException(nameof(chainLevelInfoRepository));
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _blockProcessor = blockProcessor ?? throw new ArgumentNullException(nameof(blockProcessor));
            _validatorStore = validatorStore ?? throw new ArgumentNullException(nameof(validatorStore));
            _validSealerStrategy = validSealerStrategy ?? throw new ArgumentNullException(nameof(validSealerStrategy));
            _twoThirdsMajorityTransition = twoThirdsMajorityTransition;
            _blockProcessor.BlockProcessed += OnBlockProcessed;
            _blockProcessor.BlocksProcessing += OnBlocksProcessing;
            Initialize();
        }

        private void Initialize()
        {
            var hasHead = _blockTree.Head is not null;
            var level = hasHead ? _blockTree.Head.Number + 1 : 0;
            ChainLevelInfo chainLevel;
            do
            {
                level--;
                chainLevel = _chainLevelInfoRepository.LoadLevel(level);
            }
            while (chainLevel?.MainChainBlock?.IsFinalized != true && level >= 0);

            LastFinalizedBlockLevel = level;

            // This is needed if processing was stopped between processing last block and running finalization logic 
            if (hasHead)
            {
                FinalizeBlocks(_blockTree.Head?.Header);
            }
        }

        private void OnBlocksProcessing(object? sender, BlocksProcessingEventArgs e)
        {
            void UnFinalizeBlock(BlockHeader blockHeader, BatchWrite batch)
            {
                var (chainLevel, blockInfo) = GetBlockInfo(blockHeader);
                blockInfo.IsFinalized = false;
                _chainLevelInfoRepository.PersistLevel(blockHeader.Number, chainLevel, batch);
            }

            // rerunning block
            BlockHeader header = e.Blocks[0].Header;
            if (_blockTree.WasProcessed(header.Number, header.Hash))
            {
                using (var batch = _chainLevelInfoRepository.StartBatch())
                {
                    // need to un-finalize blocks
                    var minSealersForFinalization = GetMinSealersForFinalization(header.Number);
                    for (int i = 1; i < minSealersForFinalization; i++)
                    {
                        header = _blockTree.FindParentHeader(header, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
                        if (header is not null)
                        {
                            UnFinalizeBlock(header, batch);
                        }
                    }

                    for (int i = 0; i < e.Blocks.Count; i++)
                    {
                        UnFinalizeBlock(e.Blocks[i].Header, batch);
                    }
                }
            }
        }

        private void OnBlockProcessed(object sender, BlockProcessedEventArgs e)
        {
            FinalizeBlocks(e.Block.Header);
        }

        private void FinalizeBlocks(BlockHeader finalizingBlock)
        {
            var finalizedBlocks = GetFinalizedBlocks(finalizingBlock);

            if (finalizedBlocks.Any())
            {
                if (_logger.IsTrace) _logger.Trace(finalizedBlocks.Count == 1
                        ? $"Blocks finalized by {finalizingBlock.ToString(BlockHeader.Format.FullHashAndNumber)}: {finalizedBlocks[0].ToString(BlockHeader.Format.FullHashAndNumber)}."
                        : $"Blocks finalized by {finalizingBlock.ToString(BlockHeader.Format.FullHashAndNumber)}: {finalizedBlocks[0].Number}-{finalizedBlocks[finalizedBlocks.Count - 1].Number} [{string.Join(",", finalizedBlocks.Select(b => b.Hash))}].");

                LastFinalizedBlockLevel = finalizedBlocks[^1].Number;
                BlocksFinalized?.Invoke(this, new FinalizeEventArgs(finalizingBlock, finalizedBlocks));
            }
        }

        private IReadOnlyList<BlockHeader> GetFinalizedBlocks(BlockHeader block)
        {
            if (block.Number == _twoThirdsMajorityTransition)
            {
                if (_logger.IsInfo) _logger.Info($"Block {_twoThirdsMajorityTransition}: Transitioning to 2/3 quorum.");
            }

            var minSealersForFinalization = GetMinSealersForFinalization(block.Number);
            var originalBlock = block;

            bool IsConsecutiveBlock() => originalBlock.ParentHash == _lastProcessedBlockHash;
            bool ConsecutiveBlockWillFinalizeBlocks() => _consecutiveValidatorsForNotYetFinalizedBlocks.Count >= minSealersForFinalization;

            List<BlockHeader> finalizedBlocks;
            var isConsecutiveBlock = IsConsecutiveBlock();
            HashSet<Address> validators = null;
            bool iterateThroughBlocks = true;
            // For consecutive blocks we can do a lot of optimizations.
            if (isConsecutiveBlock)
            {
                _consecutiveValidatorsForNotYetFinalizedBlocks.Add(block);

                // if block is consecutive than we can just check if this sealer will cause any blocks get finalized
                // using cache of validators of not yet finalized blocks from previous block operation
                iterateThroughBlocks = ConsecutiveBlockWillFinalizeBlocks();

                if (iterateThroughBlocks)
                {
                    // if its consecutive block we already checked there will be finalization of some blocks. Lets start processing directly from the first block that will be finalized.
                    block = _consecutiveValidatorsForNotYetFinalizedBlocks.GetBlockThatWillBeFinalized(out validators, minSealersForFinalization) ?? block;
                }
            }
            else
            {
                _consecutiveValidatorsForNotYetFinalizedBlocks.Clear();
                validators = new HashSet<Address>();
            }

            if (iterateThroughBlocks)
            {
                finalizedBlocks = new List<BlockHeader>();
                var originalBlockSealer = originalBlock.Beneficiary;
                bool ancestorsNotYetRemoved = true;

                using (var batch = _chainLevelInfoRepository.StartBatch())
                {
                    var (chainLevel, blockInfo) = GetBlockInfo(block);

                    // if this block sealer seals for 2nd time than this seal can not finalize any blocks
                    // as the 1st seal or some seal between 1st seal and current one would already finalize some of them
                    bool OriginalBlockSealerSignedOnlyOnce() => !validators.Contains(originalBlockSealer) || block.Beneficiary != originalBlockSealer;

                    while (!blockInfo.IsFinalized && (isConsecutiveBlock || OriginalBlockSealerSignedOnlyOnce()))
                    {
                        validators.Add(block.Beneficiary);
                        if (validators.Count >= minSealersForFinalization)
                        {
                            blockInfo.IsFinalized = true;
                            _chainLevelInfoRepository.PersistLevel(block.Number, chainLevel, batch);

                            finalizedBlocks.Add(block);
                            if (ancestorsNotYetRemoved)
                            {
                                _consecutiveValidatorsForNotYetFinalizedBlocks.RemoveAncestors(block.Number);
                                ancestorsNotYetRemoved = false;
                            }
                        }
                        else
                        {
                            _consecutiveValidatorsForNotYetFinalizedBlocks.Add(block);
                        }

                        if (!block.IsGenesis)
                        {
                            block = _blockTree.FindParentHeader(block, BlockTreeLookupOptions.None);
                            (chainLevel, blockInfo) = GetBlockInfo(block);
                        }
                    }
                }

                finalizedBlocks.Reverse(); // we were adding from the last to earliest, going through parents
            }
            else
            {
                finalizedBlocks = Empty;
            }

            _lastProcessedBlockHash = originalBlock.Hash;

            return finalizedBlocks;
        }

        private (ChainLevelInfo parentLevel, BlockInfo parentBlockInfo) GetBlockInfo(BlockHeader blockHeader)
        {
            var chainLevelInfo = _chainLevelInfoRepository.LoadLevel(blockHeader.Number);
            var blockInfo = chainLevelInfo.BlockInfos.First(i => i.BlockHash == blockHeader.Hash);
            return (chainLevelInfo, blockInfo);
        }

        /* Simple, unoptimized method implementation for reference: 
        private IReadOnlyList<BlockHeader> GetFinalizedBlocks(BlockHeader block)
        {
            (ChainLevelInfo parentLevel, BlockInfo parentBlockInfo) GetBlockInfo(BlockHeader blockHeader)
            {
                var chainLevelInfo = _blockInfoRepository.LoadLevel(blockHeader.Number);
                var blockInfo = chainLevelInfo.BlockInfos.First(i => i.BlockHash == blockHeader.Hash);
                return (chainLevelInfo, blockInfo);
            }

            List<BlockHeader> finalizedBlocks = new List<BlockHeader>();
            var validators = new HashSet<Address>();
            var minSealersForFinalization = block.IsGenesis ? 1 : _auRaValidator.MinSealersForFinalization;
            var originalBlockSealer = block.Beneficiary;
            
            using (var batch = _blockInfoRepository.StartBatch())
            {
                var (chainLevel, blockInfo) = GetBlockInfo(block);
                
                while (!blockInfo.IsFinalized)
                {
                    validators.Add(block.Beneficiary);
                    if (validators.Count >= minSealersForFinalization)
                    {
                        blockInfo.IsFinalized = true;
                        _blockInfoRepository.PersistLevel(block.Number, chainLevel, batch);
                        finalizedBlocks.Add(block);
                    }

                    if (!block.IsGenesis)
                    {
                        block = _blockTree.FindHeader(block.ParentHash, BlockTreeLookupOptions.None);
                        (chainLevel, blockInfo) = GetBlockInfo(block);
                    }
                }
            }

            finalizedBlocks.Reverse(); // we were adding from the last to earliest, going through parents
            
            return finalizedBlocks;
        }
        */

        public event EventHandler<FinalizeEventArgs> BlocksFinalized;

        public long GetLastLevelFinalizedBy(Keccak blockHash)
        {
            var block = _blockTree.FindHeader(blockHash, BlockTreeLookupOptions.None);
            var validators = new HashSet<Address>();
            var minSealersForFinalization = GetMinSealersForFinalization(block.Number);
            while (block.Number > 0)
            {
                validators.Add(block.Beneficiary);
                if (validators.Count >= minSealersForFinalization)
                {
                    return block.Number;
                }

                block = _blockTree.FindParentHeader(block, BlockTreeLookupOptions.None);
            }

            return 0;
        }

        public long? GetFinalizationLevel(long level)
        {
            BlockHeader? block = _blockTree.FindHeader(level, BlockTreeLookupOptions.None);
            var validators = new HashSet<Address>();
            var minSealersForFinalization = GetMinSealersForFinalization(level);

            // this can only happen when we are fast syncing headers before pivot
            if (block is null)
            {
                // in that case check if it has enough blocks to best known to be finalized
                // as everything before pivot should be finalized
                long blocksAfter = _blockTree.BestKnownNumber - level + 1;
                if (blocksAfter >= minSealersForFinalization)
                {
                    return level + minSealersForFinalization - 1;
                }
            }

            while (block is not null)
            {
                validators.Add(block.Beneficiary);
                if (validators.Count >= minSealersForFinalization)
                {
                    return block.Number;
                }

                block = _blockTree.FindHeader(block.Number + 1, BlockTreeLookupOptions.None);
            }

            return null;
        }

        private int GetMinSealersForFinalization(long blockNumber) =>
            blockNumber == 0
                ? 1
                : _validatorStore.GetValidators(blockNumber).MinSealersForFinalization(blockNumber >= _twoThirdsMajorityTransition);

        public long LastFinalizedBlockLevel
        {
            get => _lastFinalizedBlockLevel;
            private set
            {
                if (_lastFinalizedBlockLevel < value)
                {
                    _lastFinalizedBlockLevel = value;
                    if (_logger.IsTrace) _logger.Trace($"Setting {nameof(LastFinalizedBlockLevel)} to {value}.");
                }
            }
        }

        public void Dispose()
        {
            _blockProcessor.BlockProcessed -= OnBlockProcessed;
        }

        [DebuggerDisplay("Count = {Count}")]
        private class ValidationStampCollection
        {
            private readonly IDictionary<Address, int> _validatorCount = new Dictionary<Address, int>();
            private readonly Deque<BlockHeader> _blocks = new Deque<BlockHeader>();

            public int Count => _validatorCount.Count;

            public void Add(BlockHeader blockHeader)
            {
                bool DoesNotContainBlock() => _blocks.Count == 0 || _blocks[0].Number > blockHeader.Number || _blocks[^1].Number < blockHeader.Number;

                if (DoesNotContainBlock())
                {
                    if (_blocks.Count == 0 || _blocks[0].Number < blockHeader.Number)
                    {
                        _blocks.AddToFront(blockHeader);
                    }
                    else
                    {
                        _blocks.AddToBack(blockHeader);
                    }
                    int count = _validatorCount.TryGetValue(blockHeader.Beneficiary, out count) ? count + 1 : 1;
                    _validatorCount[blockHeader.Beneficiary] = count;
                }
            }

            public void RemoveAncestors(long blockNumber)
            {
                for (int i = _blocks.Count - 1; i >= 0; i--)
                {
                    var item = _blocks[i];
                    if (item.Number <= blockNumber)
                    {
                        _blocks.RemoveFromBack();
                        var setCount = _validatorCount[item.Beneficiary];
                        if (setCount == 1)
                        {
                            _validatorCount.Remove(item.Beneficiary);
                        }
                        else
                        {
                            _validatorCount[item.Beneficiary] = setCount - 1;
                        }
                    }
                    else
                    {
                        break;
                    }
                }
            }

            public void Clear()
            {
                _validatorCount.Clear();
                _blocks.Clear();
            }

            public BlockHeader GetBlockThatWillBeFinalized(out HashSet<Address> validators, int minSealersForFinalization)
            {
                validators = new HashSet<Address>();
                for (int i = 0; i < _blocks.Count; i++)
                {
                    var block = _blocks[i];
                    validators.Add(block.Beneficiary);
                    if (validators.Count >= minSealersForFinalization)
                    {
                        return block;
                    }
                }

                return null;
            }
        }
    }
}
