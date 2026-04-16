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
        private static readonly List<BlockHeader> Empty = new();
        private readonly IBlockTree _blockTree;
        private readonly IChainLevelInfoRepository _chainLevelInfoRepository;
        private readonly ILogger _logger;
        private IBranchProcessor? _branchProcessor;
        private readonly IValidatorStore _validatorStore;
        private readonly long _twoThirdsMajorityTransition;
        private Hash256 _lastProcessedBlockHash = Keccak.EmptyTreeHash;
        private readonly ValidationStampCollection _consecutiveValidatorsForNotYetFinalizedBlocks = new();

        public AuRaBlockFinalizationManager(
            IBlockTree blockTree,
            IChainLevelInfoRepository chainLevelInfoRepository,
            IValidatorStore validatorStore,
            ILogManager logManager,
            long twoThirdsMajorityTransition = long.MaxValue)
        {
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _chainLevelInfoRepository = chainLevelInfoRepository ?? throw new ArgumentNullException(nameof(chainLevelInfoRepository));
            _logger = logManager?.GetClassLogger<AuRaBlockFinalizationManager>() ?? throw new ArgumentNullException(nameof(logManager));
            _validatorStore = validatorStore ?? throw new ArgumentNullException(nameof(validatorStore));
            _twoThirdsMajorityTransition = twoThirdsMajorityTransition;
            Initialize();
        }

        public void SetMainBlockBranchProcessor(IBranchProcessor branchProcessor)
        {
            _branchProcessor = branchProcessor;
            _branchProcessor.BlockProcessed += OnBlockProcessed;
            _branchProcessor.BlocksProcessing += OnBlocksProcessing;
        }


        private void Initialize()
        {
            bool hasHead = _blockTree.Head is not null;
            long level = hasHead ? _blockTree.Head.Number + 1 : 0;
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
                (ChainLevelInfo parentLevel, BlockInfo parentBlockInfo)? info = GetBlockInfo(blockHeader);
                if (info is not null)
                {
                    (ChainLevelInfo chainLevel, BlockInfo blockInfo) = info.Value;
                    blockInfo.IsFinalized = false;
                    _chainLevelInfoRepository.PersistLevel(blockHeader.Number, chainLevel, batch);
                }
            }

            // rerunning block
            BlockHeader header = e.Blocks[0].Header;
            if (_blockTree.WasProcessed(header.Number, header.Hash!))
            {
                using BatchWrite batch = _chainLevelInfoRepository.StartBatch();
                // need to un-finalize blocks
                int minSealersForFinalization = GetMinSealersForFinalization(header.Number);
                for (int i = 1; i < minSealersForFinalization; i++)
                {
                    header = _blockTree.FindParentHeader(header!, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
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

        private void OnBlockProcessed(object sender, BlockProcessedEventArgs e) => FinalizeBlocks(e.Block.Header);

        private void FinalizeBlocks(BlockHeader finalizingBlock)
        {
            IReadOnlyList<BlockHeader> finalizedBlocks = GetFinalizedBlocks(finalizingBlock);

            if (finalizedBlocks.Count > 0)
            {
                if (_logger.IsTrace) _logger.Trace(finalizedBlocks.Count == 1
                        ? $"Blocks finalized by {finalizingBlock.ToString(BlockHeader.Format.FullHashAndNumber)}: {finalizedBlocks[0].ToString(BlockHeader.Format.FullHashAndNumber)}."
                        : $"Blocks finalized by {finalizingBlock.ToString(BlockHeader.Format.FullHashAndNumber)}: {finalizedBlocks[0].Number}-{finalizedBlocks[^1].Number} [{string.Join(",", finalizedBlocks.Select(static b => b.Hash))}].");

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

            int minSealersForFinalization = GetMinSealersForFinalization(block.Number);
            BlockHeader originalBlock = block;

            bool IsConsecutiveBlock() => originalBlock.ParentHash == _lastProcessedBlockHash;
            bool ConsecutiveBlockWillFinalizeBlocks() => _consecutiveValidatorsForNotYetFinalizedBlocks.Count >= minSealersForFinalization;

            List<BlockHeader> finalizedBlocks;
            bool isConsecutiveBlock = IsConsecutiveBlock();
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
                Address originalBlockSealer = originalBlock.Beneficiary;
                bool ancestorsNotYetRemoved = true;

                using (BatchWrite batch = _chainLevelInfoRepository.StartBatch())
                {
                    (ChainLevelInfo parentLevel, BlockInfo parentBlockInfo)? info = GetBlockInfo(block);
                    if (info is not null)
                    {
                        (ChainLevelInfo? chainLevel, BlockInfo? blockInfo) = info.Value;

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
                                if (block is null)
                                    break;
                                (ChainLevelInfo parentLevel, BlockInfo parentBlockInfo)? parentInfo = GetBlockInfo(block);
                                if (parentInfo is null)
                                    break;
                                (chainLevel, blockInfo) = parentInfo.Value;
                            }
                        }
                    }
                    else
                        return Empty;
                }

                finalizedBlocks.Reverse(); // we were adding from the last to earliest, going through parents
            }
            else
            {
                finalizedBlocks = Empty;
            }

            _lastProcessedBlockHash = originalBlock.Hash!;

            return finalizedBlocks;
        }

        private (ChainLevelInfo parentLevel, BlockInfo parentBlockInfo)? GetBlockInfo(BlockHeader blockHeader)
        {
            ChainLevelInfo? chainLevelInfo = _chainLevelInfoRepository.LoadLevel(blockHeader.Number);
            BlockInfo? blockInfo = chainLevelInfo?.BlockInfos.FirstOrDefault(i => i.BlockHash == blockHeader.Hash);
            return blockInfo != null ? (chainLevelInfo, blockInfo) : null;
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

        public event EventHandler<FinalizeEventArgs>? BlocksFinalized;

        public long GetLastLevelFinalizedBy(Hash256 blockHash)
        {
            BlockHeader block = _blockTree.FindHeader(blockHash, BlockTreeLookupOptions.None)!;
            HashSet<Address> validators = new();
            int minSealersForFinalization = GetMinSealersForFinalization(block.Number);
            while (block!.Number > 0)
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
            HashSet<Address> validators = new();
            int minSealersForFinalization = GetMinSealersForFinalization(level);

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
            get;
            private set
            {
                if (field < value)
                {
                    field = value;
                    if (_logger.IsTrace) _logger.Trace($"Setting {nameof(LastFinalizedBlockLevel)} to {value}.");
                }
            }
        }

        public void Dispose()
        {
            if (_branchProcessor is not null)
            {
                _branchProcessor.BlockProcessed -= OnBlockProcessed;
                _branchProcessor.BlocksProcessing -= OnBlocksProcessing;
            }
        }

        [DebuggerDisplay("Count = {Count}")]
        private class ValidationStampCollection
        {
            private readonly IDictionary<Address, int> _validatorCount = new Dictionary<Address, int>();
            private readonly Deque<BlockHeader> _blocks = new();

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
                    int count = _validatorCount.TryGetValue(blockHeader.Beneficiary!, out count) ? count + 1 : 1;
                    _validatorCount[blockHeader.Beneficiary] = count;
                }
            }

            public void RemoveAncestors(long blockNumber)
            {
                for (int i = _blocks.Count - 1; i >= 0; i--)
                {
                    BlockHeader item = _blocks[i];
                    if (item.Number <= blockNumber)
                    {
                        _blocks.RemoveFromBack();
                        int setCount = _validatorCount[item.Beneficiary!];
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

            public BlockHeader? GetBlockThatWillBeFinalized(out HashSet<Address> validators, int minSealersForFinalization)
            {
                validators = new HashSet<Address>();
                for (int i = 0; i < _blocks.Count; i++)
                {
                    BlockHeader? block = _blocks[i];
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
