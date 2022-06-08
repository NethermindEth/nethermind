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
using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Blockchain;
using Nethermind.Consensus;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Evm.Tracing;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.Data.V1;
using Nethermind.Merge.Plugin.InvalidChainTracker;
using Nethermind.Merge.Plugin.Synchronization;
using Nethermind.Synchronization;

namespace Nethermind.Merge.Plugin.Handlers.V1
{
    /// <summary>
    /// Verifies the payload according to the execution environment rule set (EIP-3675) and returns the <see cref="PayloadStatusV1"/> of the verification and the hash of the last valid block.
    /// 
    /// <seealso cref="https://github.com/ethereum/execution-apis/blob/main/src/engine/specification.md#engine_newpayloadv1"/>
    /// </summary>
    public class NewPayloadV1Handler : IAsyncHandler<ExecutionPayloadV1, PayloadStatusV1>
    {
        private readonly IBlockValidator _blockValidator;
        private readonly IBlockTree _blockTree;
        private readonly IBlockchainProcessor _processor;
        private readonly IPoSSwitcher _poSSwitcher;
        private readonly IBeaconSyncStrategy _beaconSyncStrategy;
        private readonly IBeaconPivot _beaconPivot;
        private readonly IBlockCacheService _blockCacheService;
        private readonly IBlockProcessingQueue _processingQueue;
        private readonly IMergeSyncController _mergeSyncController;
        private readonly IInvalidChainTracker _invalidChainTracker;
        private readonly ILogger _logger;
        private readonly LruCache<Keccak, bool> _latestBlocks = new(50, "LatestBlocks");
        private readonly ProcessingOptions _processingOptions;

        public NewPayloadV1Handler(
            IBlockValidator blockValidator,
            IBlockTree blockTree,
            IBlockchainProcessor processor,
            IInitConfig initConfig,
            IPoSSwitcher poSSwitcher,
            IBeaconSyncStrategy beaconSyncStrategy,
            IBeaconPivot beaconPivot,
            IBlockCacheService blockCacheService,
            IBlockProcessingQueue processingQueue,
            IInvalidChainTracker invalidChainTracker,
            IMergeSyncController mergeSyncController,
            ILogManager logManager)
        {
            _blockValidator = blockValidator ?? throw new ArgumentNullException(nameof(blockValidator));
            _blockTree = blockTree;
            _processor = processor;
            _poSSwitcher = poSSwitcher;
            _beaconSyncStrategy = beaconSyncStrategy;
            _beaconPivot = beaconPivot;
            _blockCacheService = blockCacheService;
            _processingQueue = processingQueue;
            _invalidChainTracker = invalidChainTracker; 
            _mergeSyncController = mergeSyncController;
            _logger = logManager.GetClassLogger();
            _processingOptions = initConfig.StoreReceipts ? ProcessingOptions.EthereumMerge | ProcessingOptions.StoreReceipts : ProcessingOptions.EthereumMerge; 
        }

        public async Task<ResultWrapper<PayloadStatusV1>> HandleAsync(ExecutionPayloadV1 request)
        {
            string requestStr = $"a new payload: {request}";
            if (_logger.IsInfo) { _logger.Info($"Received {requestStr}"); }

            if (!request.TryGetBlock(out Block? block, _poSSwitcher.FinalTotalDifficulty))
            {
                if (_logger.IsWarn) _logger.Warn($"Invalid block. Result of {requestStr}.");
                return NewPayloadV1Result.Invalid(null, $"Block {request} could not be parsed as a block");
            }

            if (!HeaderValidator.ValidateHash(block.Header))
            {
                if (_logger.IsWarn) _logger.Warn($"InvalidBlockHash. Result of {requestStr}.");
                return NewPayloadV1Result.InvalidBlockHash;
            }
            
            _invalidChainTracker.SetChildParent(request.BlockHash, request.ParentHash);
            if (_invalidChainTracker.IsOnKnownInvalidChain(request.BlockHash, out Keccak? lastValidHash))
            {
                return NewPayloadV1Result.Invalid(lastValidHash, $"Block {request} is known to be a part of an invalid chain.");
            }
            
            block.Header.TotalDifficulty = _poSSwitcher.FinalTotalDifficulty;
            // ToDo if block is below syncPivot, we can return SYNCING and ignore block

            BlockHeader? parentHeader = _blockTree.FindHeader(request.ParentHash, BlockTreeLookupOptions.None);
            if (parentHeader is null)
            {
                // possible that headers sync finished before this was called, so blocks in cache weren't inserted
                if (!_beaconSyncStrategy.IsBeaconSyncFinished(parentHeader))
                {
                    bool inserted = TryInsertDanglingBlock(block);
                    if (_logger.IsInfo) _logger.Info(inserted ? $"BeaconSync not finished - block {block} inserted" : $"BeaconSync not finished - block {block} accepted.");
                    return inserted ? NewPayloadV1Result.Syncing : NewPayloadV1Result.Accepted;
                }

                if (_logger.IsInfo) _logger.Info($"Insert block into cache without parent {block}");
                _blockCacheService.BlockCache.TryAdd(request.BlockHash, block);
                return NewPayloadV1Result.Accepted;
            }
            
            // we need to check if the head is greater than block.Number. In fast sync we could return Valid to CL without this if
            if (_blockTree.IsOnMainChainBehindOrEqualHead(block))
            {
                if (_logger.IsInfo) _logger.Info($"Valid... A new payload ignored. Block {block.ToString(Block.Format.FullHashAndNumber)} found in main chain.");
                return NewPayloadV1Result.Valid(block.Hash);
            }

            bool parentProcessed = _blockTree.WasProcessed(parentHeader.Number, parentHeader.GetOrCalculateHash());
            if (!parentProcessed)
            {
                BlockTreeInsertOptions insertOptions = BlockTreeInsertOptions.BeaconBlockInsert;
                _blockTree.Insert(block, true, insertOptions);
                if (_logger.IsInfo) _logger.Info("Syncing... Parent wasn't processed. Inserting block.");
                return NewPayloadV1Result.Syncing;
            }

            if (_poSSwitcher.MisconfiguredTerminalTotalDifficulty() || _poSSwitcher.BlockBeforeTerminalTotalDifficulty(parentHeader))
            {
                if (_logger.IsWarn) _logger.Warn($"Invalid terminal block. Nethermind TTD {_poSSwitcher.TerminalTotalDifficulty}, Parent TD: {parentHeader!.TotalDifficulty}. Request: {requestStr}.");
                
                // {status: INVALID, latestValidHash: 0x0000000000000000000000000000000000000000000000000000000000000000, validationError: errorMessage | null} if terminal block conditions are not satisfied
                return NewPayloadV1Result.Invalid(Keccak.Zero);
            }

            // Otherwise, we can just process this block and we don't need to do BeaconSync anymore.
            _mergeSyncController.StopSyncing();
            
            // Try to execute block
            ValidationResult result = ValidateBlockAndProcess(block, parentHeader, out Block? processedBlock, out string? message);
            
            if ((result & ValidationResult.AlreadyKnown) == ValidationResult.AlreadyKnown || result == ValidationResult.Invalid)
            {
                bool isValid = (result & ValidationResult.Valid) == ValidationResult.Valid;
                if (_logger.IsInfo)
                {
                    string resultStr = isValid ? "Valid" : "Invalid";
                    if (_logger.IsInfo) _logger.Info($"{resultStr}. Result of {requestStr}.");
                }

                if (!isValid)
                {
                    _invalidChainTracker.OnInvalidBlock(request.BlockHash, request.ParentHash);
                }

                return ResultWrapper<PayloadStatusV1>.Success(BuildExecutePayloadResult(request, isValid, parentHeader, message));
            }

            if (result == ValidationResult.Syncing)
            {
                if (_logger.IsInfo) _logger.Info($"Processing queue wasn't empty added to queue {requestStr}.");
                return NewPayloadV1Result.Syncing;
            }

            if (processedBlock is null)
            {
                if (_logger.IsInfo) _logger.Info($"Invalid block processed. Result of {requestStr}.");
                return ResultWrapper<PayloadStatusV1>.Success(BuildExecutePayloadResult(request, false, parentHeader, $"Processed block is null, request {request}"));
            }

            if (_logger.IsInfo) _logger.Info($"Valid. Result of {requestStr}.");
            return NewPayloadV1Result.Valid(request.BlockHash);
        }

        private ValidationResult ValidateBlockAndProcess(Block block, BlockHeader parent, out Block? processedBlock, out string? validationMessage)
        {
            ValidationResult ToValid(bool valid) => valid ? ValidationResult.Valid : ValidationResult.Invalid;
            
            validationMessage = null;
            processedBlock = null;

            // If duplicate, reuse results
            bool isRecentBlock = _latestBlocks.TryGet(block.Hash!, out bool isValid);
            if (isRecentBlock)
            {
                if (!isValid && _logger.IsWarn)
                {
                    validationMessage = $"Invalid block {block} sent from latestBlock cache";
                    if (_logger.IsWarn) _logger.Warn(validationMessage);
                }

                return ValidationResult.AlreadyKnown | ToValid(isValid);
            }

            // Validate
            bool validAndProcessed = ValidateWithBlockValidator(block, parent);
            if (validAndProcessed)
            {
                if (_processingQueue.IsEmpty)
                {
                    // processingQueue is empty so we can process the block in synchronous way
                    _blockTree.SuggestBlock(block, BlockTreeSuggestOptions.None, false);
                    processedBlock = _processor.Process(block, _processingOptions, NullBlockTracer.Instance);
                }
                else
                {
                    // this is needed for restarts. We have blocks in queue so we should add it to queue and return SYNCING
                    _blockTree.SuggestBlock(block);
                    return ValidationResult.Syncing;
                }
            
                if (processedBlock is null)
                {
                    if (_logger.IsWarn) _logger.Warn($"Block {block.ToString(Block.Format.FullHashAndNumber)} cannot be processed and wont be accepted to the tree.");
                    validAndProcessed = false;
                }
            }

            _latestBlocks.Set(block.Hash!, validAndProcessed);
            return ToValid(validAndProcessed);
        }

        private bool ValidateWithBlockValidator(Block block, BlockHeader parent)
        {
            block.Header.TotalDifficulty ??= parent.TotalDifficulty + block.Difficulty;
            block.Header.IsPostMerge = true; // I think we don't need to set it again here.
            bool isValid = _blockValidator.ValidateSuggestedBlock(block);
            if (!isValid && _logger.IsWarn) _logger.Warn($"Block validator rejected the block {block.ToString(Block.Format.FullHashAndNumber)}.");
            return isValid;
        }

        private PayloadStatusV1 BuildExecutePayloadResult(ExecutionPayloadV1 request, bool isValid, BlockHeader? parent, string? validationMessage)
        {
            PayloadStatusV1 payloadStatus = new();
            if (isValid)
            {
                payloadStatus.Status = PayloadStatus.Valid;
                payloadStatus.LatestValidHash = request.BlockHash;
            }
            else
            {
                payloadStatus.ValidationError = validationMessage;
                payloadStatus.Status = PayloadStatus.Invalid;
                if (_invalidChainTracker.IsOnKnownInvalidChain(request.BlockHash, out Keccak? lastValidHash))
                {
                    payloadStatus.LatestValidHash = lastValidHash;
                }
                else
                {
                    if (parent != null)
                    {
                        payloadStatus.LatestValidHash = request.ParentHash;
                    }
                    else
                    {
                        payloadStatus.LatestValidHash = _blockTree.HeadHash;
                    }
                }
            }

            return payloadStatus;
        }

        /// Pop blocks from cache up to ancestor on the beacon chain. Which is then inserted into the block tree
        /// which I assume will switch the canonical chain.
        /// Return false if no ancestor that is part of beacon chain found.
        private bool TryInsertDanglingBlock(Block block)
        {
            BlockTreeInsertOptions insertOptions = BlockTreeInsertOptions.BeaconBlockInsert;

            if (!_blockTree.IsKnownBeaconBlock(block.Number, block.Hash ?? block.CalculateHash()))
            {
                // last block inserted is parent of current block, part of the same chain
                if (block.ParentHash == _blockCacheService.ProcessDestination)
                {
                    _blockTree.Insert(block, true, insertOptions);
                }
                else
                {
                    Block? current = block;
                    Stack<Block> stack = new();
                    while (current != null)
                    {
                        stack.Push(current);
                        if (current.Hash == _beaconPivot.PivotHash ||
                            _blockTree.IsKnownBeaconBlock(current.Number, current.Hash))
                        {
                            break;
                        }

                        _blockCacheService.BlockCache.TryGetValue(current.ParentHash, out Block? parentBlock);
                        current = parentBlock;
                    }

                    if (current == null)
                    {
                        // block not part of beacon pivot chain, save in cache
                        _blockCacheService.BlockCache.TryAdd(block.Hash, block);
                        return false;
                    }

                    while (stack.TryPop(out Block? child))
                    {
                        _blockTree.Insert(child, true, insertOptions);
                    }
                }

                _blockCacheService.ProcessDestination = block.Hash;
            }

            return true;
        }

        [Flags]
        private enum ValidationResult
        {
            Invalid = 0,
            Valid = 1,
            AlreadyKnown = 2,
            Syncing = 3
        }
    }
}
