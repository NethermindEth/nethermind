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
using Nethermind.Store;
using Nethermind.Store.Repositories;

namespace Nethermind.AuRa
{
    public class AuRaBlockFinalizationManager : IBlockFinalizationManager
    {
        private static readonly List<BlockHeader> Empty = new List<BlockHeader>();
        private readonly IBlockTree _blockTree;
        private readonly IChainLevelInfoRepository _chainLevelInfoRepository;
        private readonly IAuRaValidator _auRaValidator;
        private readonly ILogger _logger;
        private readonly IBlockProcessor _blockProcessor;
        private long _lastFinalizedBlockLevel = -1L;
        private Keccak _lastProcessedBlockHash = Keccak.EmptyTreeHash;
        private readonly ValidationStampCollection _consecutiveValidatorsForNotYetFinalizedBlocks = new ValidationStampCollection();

        public AuRaBlockFinalizationManager(IBlockTree blockTree, IChainLevelInfoRepository chainLevelInfoRepository, IBlockProcessor blockProcessor, IAuRaValidator auRaValidator, ILogManager logManager)
        {
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _chainLevelInfoRepository = chainLevelInfoRepository ?? throw new ArgumentNullException(nameof(chainLevelInfoRepository));
            _auRaValidator = auRaValidator ?? throw new ArgumentNullException(nameof(auRaValidator));
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _blockProcessor = blockProcessor ?? throw new ArgumentNullException(nameof(blockProcessor));
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
            
            _auRaValidator.SetFinalizationManager(this);
            
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
                        ? $"Finalizing block {finalizedBlocks[0].Number} by block {finalizingBlock.Number}."
                        : $"Finalizing blocks {finalizedBlocks[0].Number}-{finalizedBlocks[finalizedBlocks.Count - 1].Number} by block {finalizingBlock.Number}.");
                
                BlocksFinalized?.Invoke(this, new FinalizeEventArgs(finalizingBlock, finalizedBlocks));
                LastFinalizedBlockLevel = finalizedBlocks[finalizedBlocks.Count - 1].Number;
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
            
            var minSealersForFinalization = block.IsGenesis ? 1 : _auRaValidator.MinSealersForFinalization;
            var originalBlock = block;
            
            bool IsConsecutiveBlock()
            {
                return originalBlock.ParentHash == _lastProcessedBlockHash;
            }

            bool ConsecutiveBlockWillFinalizeBlocks()
            {
                _consecutiveValidatorsForNotYetFinalizedBlocks.Add(block);
                return _consecutiveValidatorsForNotYetFinalizedBlocks.Count >= minSealersForFinalization;
            }

            List<BlockHeader> finalizedBlocks;
            var isConsecutiveBlock = IsConsecutiveBlock();
            
            // Optimization:
            // if block is consecutive than we can just check if this sealer will cause any blocks get finalized
            // using cache of vallidators of not yet finalized blocks from previous block operation
            if (isConsecutiveBlock && !ConsecutiveBlockWillFinalizeBlocks())
            {
                finalizedBlocks = Empty;
            }
            else
            {
                if (!isConsecutiveBlock)
                {
                    _consecutiveValidatorsForNotYetFinalizedBlocks.Clear();
                }

                finalizedBlocks = new List<BlockHeader>();
                var validators = new HashSet<Address>();
                var originalBlockSealer = originalBlock.Beneficiary;

                using (var batch = _chainLevelInfoRepository.StartBatch())
                {
                    var (chainLevel, blockInfo) = GetBlockInfo(block);

                    // Optimization:
                    // if this block sealer seals for 2nd time than this seal can not finalize any blocks
                    // as the 1st seal or some seal between 1st seal and current one would already finalize some of them
                    bool OriginalBlockSealerSignedOnlyOnce() => !validators.Contains(originalBlockSealer) || block.Beneficiary != originalBlockSealer;
                    
                    while (!blockInfo.IsFinalized && OriginalBlockSealerSignedOnlyOnce())
                    {
                        validators.Add(block.Beneficiary);
                        if (validators.Count >= minSealersForFinalization)
                        {
                            blockInfo.IsFinalized = true;
                            _chainLevelInfoRepository.PersistLevel(block.Number, chainLevel, batch);
                            finalizedBlocks.Add(block);
                            _consecutiveValidatorsForNotYetFinalizedBlocks.RemoveAncestors(block.Number);
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

            _lastProcessedBlockHash = originalBlock.Hash;

            return finalizedBlocks;
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
            private readonly ISet<Address> _set = new HashSet<Address>();
            private readonly SortedList<long, Address> _list = new SortedList<long, Address>(Comparer<long>.Create((x, y) => y.CompareTo(x)));

            public int Count => _set.Count;
            
            public void Add(BlockHeader blockHeader)
            {
                if (_set.Add(blockHeader.Beneficiary))
                {
                    _list.Add(blockHeader.Number, blockHeader.Beneficiary);
                }
            }

            public void RemoveAncestors(long blockNumber)
            {
                IEnumerable<long> toDelete = _list.Keys.SkipWhile(k => k > blockNumber).ToArray();
                foreach (var number in toDelete)
                {
                    _set.Remove(_list[number]);
                    _list.Remove(number);
                }
            }

            public void Clear()
            {
                _set.Clear();;
                _list.Clear();
            }
        }
    }
}