//  Copyright (c) 2018 Demerzel Solutions Limited
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

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Nethermind.AuRa.Validators;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Store.Repositories;
using Nito.Collections;

namespace Nethermind.AuRa.Test
{
    public class AuRaBlockFinalizationManager : IBlockFinalizationManager
    {
        private static readonly List<BlockHeader> Empty = new List<BlockHeader>();
        private readonly IBlockTree _blockTree;
        private readonly IChainLevelInfoRepository _chainLevelInfoRepository;
        private readonly ILogger _logger;
        private readonly IBlockProcessor _blockProcessor;
        private readonly IValidatorStore _validatorStore;
        private readonly IValidSealerStrategy _validSealerStrategy;
        private long _lastFinalizedBlockLevel;
        private Keccak _lastProcessedBlockHash = Keccak.EmptyTreeHash;
        private readonly ValidationStampCollection _consecutiveValidatorsForNotYetFinalizedBlocks = new ValidationStampCollection();

        public AuRaBlockFinalizationManager(IBlockTree blockTree, IChainLevelInfoRepository chainLevelInfoRepository, IBlockProcessor blockProcessor, IValidatorStore validatorStore, IValidSealerStrategy validSealerStrategy, ILogManager logManager)
        {
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _chainLevelInfoRepository = chainLevelInfoRepository ?? throw new ArgumentNullException(nameof(chainLevelInfoRepository));
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _blockProcessor = blockProcessor ?? throw new ArgumentNullException(nameof(blockProcessor));
            _validatorStore = validatorStore ?? throw new ArgumentNullException(nameof(validatorStore));
            _validSealerStrategy = validSealerStrategy ?? throw new ArgumentNullException(nameof(validSealerStrategy));
            _blockProcessor.BlockProcessed += OnBlockProcessed;
            Initialize();
        }

        private void Initialize()
        {
            var hasHead = _blockTree.Head != null;
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
                FinalizeBlocks(_blockTree.Head);
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
                if (_logger.IsDebug) _logger.Debug(finalizedBlocks.Count == 1
                        ? $"Blocks finalized by {finalizingBlock.ToString(BlockHeader.Format.FullHashAndNumber)}: {finalizedBlocks[0].ToString(BlockHeader.Format.FullHashAndNumber)}."
                        : $"Blocks finalized by {finalizingBlock.ToString(BlockHeader.Format.FullHashAndNumber)}: {finalizedBlocks[0].Number}-{finalizedBlocks[finalizedBlocks.Count - 1].Number} [{string.Join(",", finalizedBlocks.Select(b => b.Hash))}].");
                
                LastFinalizedBlockLevel = finalizedBlocks[^1].Number;
                BlocksFinalized?.Invoke(this, new FinalizeEventArgs(finalizingBlock, finalizedBlocks));
            }
        }
        
        private IReadOnlyList<BlockHeader> GetFinalizedBlocks(BlockHeader block)
        {
            (ChainLevelInfo parentLevel, BlockInfo parentBlockInfo) GetBlockInfo(BlockHeader blockHeader)
            {
                var chainLevelInfo = _chainLevelInfoRepository.LoadLevel(blockHeader.Number);
                var blockInfo = chainLevelInfo.BlockInfos.First(i => i.BlockHash == blockHeader.Hash);
                return (chainLevelInfo, blockInfo);
            }
            
            var minSealersForFinalization = block.IsGenesis ? 1 : Validators.MinSealersForFinalization();
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
                            block = _blockTree.FindHeader(block.ParentHash, BlockTreeLookupOptions.None);
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

        private Address[] Validators => _validatorStore.GetValidators();

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
        
        public long GetLastLevelFinalizedBy(Keccak headHash)
        {
            var block = _blockTree.FindHeader(headHash, BlockTreeLookupOptions.None);
            var validators = new HashSet<Address>();
            var minSealersForFinalization = Validators.MinSealersForFinalization();
            while (block.Number > 0)
            {
                validators.Add(block.Beneficiary);
                if (validators.Count >= minSealersForFinalization)
                {
                    return block.Number;
                }

                block = _blockTree.FindHeader(block.ParentHash, BlockTreeLookupOptions.None);
            }
            
            return 0;
        }

        public long? GetFinalizedLevel(long blockLevel)
        {
            BlockInfo GetBlockInfo(long level)
            {
                var chainLevelInfo = _chainLevelInfoRepository.LoadLevel(level);
                return  chainLevelInfo?.MainChainBlock ?? chainLevelInfo?.BlockInfos[0];
            }

            var validators = new HashSet<Address>();
            var minSealersForFinalization = Validators.MinSealersForFinalization();
            var blockInfo = GetBlockInfo(blockLevel);
            while (blockInfo != null)
            {
                var block = _blockTree.FindHeader(blockInfo.BlockHash, BlockTreeLookupOptions.None);
                if (_validSealerStrategy.IsValidSealer(Validators, block.Beneficiary, block.AuRaStep.Value))
                {
                    validators.Add(block.Beneficiary);
                    if (validators.Count >= minSealersForFinalization)
                    {
                        return block.Number;
                    }
                }

                blockLevel++;
                blockInfo = GetBlockInfo(blockLevel);
            }

            return null;
        }

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
                _validatorCount.Clear();;
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