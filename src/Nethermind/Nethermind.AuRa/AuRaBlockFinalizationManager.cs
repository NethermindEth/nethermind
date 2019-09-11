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
using System.Linq;
using Nethermind.AuRa.Validators;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Logging;
using Nethermind.Store;
using Nethermind.Store.Repositories;

namespace Nethermind.AuRa
{
    public class AuRaBlockFinalizationManager : IBlockFinalizationManager
    {
        private readonly IBlockTree _blockTree;
        private readonly IBlockInfoRepository _blockInfoRepository;
        private readonly IAuRaValidator _auRaValidator;
        private readonly ILogger _logger;
        private IBlockProcessor _blockProcessor;
        private long _lastFinalizedBlockLevel = -1L;
        
        public AuRaBlockFinalizationManager(IBlockTree blockTree, IBlockInfoRepository blockInfoRepository, IBlockProcessor blockProcessor, IAuRaValidator auRaValidator, ILogManager logManager)
        {
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _blockInfoRepository = blockInfoRepository ?? throw new ArgumentNullException(nameof(blockInfoRepository));
            _auRaValidator = auRaValidator ?? throw new ArgumentNullException(nameof(auRaValidator));
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _blockProcessor = blockProcessor ?? throw new ArgumentNullException(nameof(blockProcessor));
            _blockProcessor.BlockProcessed += OnBlockProcessed;
            InitLastFinalizedBLock();
            auRaValidator.SetFinalizationManager(this);
        }

        private void InitLastFinalizedBLock()
        {
            var level = _blockTree.Head == null ? 0 : _blockTree.Head.Number + 1;
            ChainLevelInfo chainLevel;
            do
            {
                level--;
                chainLevel = _blockInfoRepository.LoadLevel(level);
            } 
            while (chainLevel?.MainChainBlock?.IsFinalized != true && level >= 0);

            LastFinalizedBlockLevel = level;
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

        [Todo(Improve.Performance, "Optimize if there are no reorganisations with object cache.")]
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
                
                bool OriginalBlockSealerSignedOnlyOnce() => !validators.Contains(originalBlockSealer) || block.Beneficiary != originalBlockSealer; // if this block sealer seals for 2nd time than this seal can not finalize any blocks

                while (!blockInfo.IsFinalized && OriginalBlockSealerSignedOnlyOnce())
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

        public event EventHandler<FinalizeEventArgs> BlocksFinalized;
        public event EventHandler LastFinalizedBlockLevelChanged;

        public long LastFinalizedBlockLevel
        {
            get => _lastFinalizedBlockLevel;
            private set
            {
                if (_lastFinalizedBlockLevel < value)
                {
                    _lastFinalizedBlockLevel = value;
                    if (_logger.IsTrace) _logger.Trace($"Setting {nameof(LastFinalizedBlockLevel)} to {value}.");
                    LastFinalizedBlockLevelChanged?.Invoke(this, EventArgs.Empty);
                }
            }
        }

        public void Dispose()
        {
            _blockProcessor.BlockProcessed -= OnBlockProcessed;
        }
    }
}