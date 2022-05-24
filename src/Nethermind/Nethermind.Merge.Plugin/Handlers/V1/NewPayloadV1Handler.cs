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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Consensus;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Evm.Tracing;
using Nethermind.Facade.Eth;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.Data.V1;
using Nethermind.Merge.Plugin.Synchronization;
using Nethermind.Synchronization;
using Nethermind.Synchronization.ParallelSync;

namespace Nethermind.Merge.Plugin.Handlers.V1
{
    /// <summary>
    /// https://hackmd.io/@n0ble/kintsugi-spec
    /// Verifies the payload according to the execution environment rule set (EIP-3675)
    /// and returns the status of the verification and the hash of the last valid block
    /// </summary>
    public class NewPayloadV1Handler : IAsyncHandler<BlockRequestResult, PayloadStatusV1>
    {
        private readonly IBlockValidator _blockValidator;
        private readonly IBlockTree _blockTree;
        private readonly IBlockchainProcessor _processor;
        private readonly IInitConfig _initConfig;
        private readonly IPoSSwitcher _poSSwitcher;
        private readonly IBeaconSyncStrategy _beaconSyncStrategy;
        private readonly IBeaconPivot _beaconPivot;
        private readonly IBlockCacheService _blockCacheService;
        private readonly IBlockProcessingQueue _processingQueue;
        private readonly IMergeSyncController _mergeSyncController;
        private readonly ILogger _logger;
        private readonly LruCache<Keccak, bool> _latestBlocks = new(50, "LatestBlocks");
        private readonly ConcurrentDictionary<Keccak, Keccak> _lastValidHashes = new();

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
            IMergeSyncController mergeSyncController,
            ILogManager logManager)
        {
            _blockValidator = blockValidator ?? throw new ArgumentNullException(nameof(blockValidator));
            _blockTree = blockTree;
            _processor = processor;
            _initConfig = initConfig;
            _poSSwitcher = poSSwitcher;
            _beaconSyncStrategy = beaconSyncStrategy;
            _beaconPivot = beaconPivot;
            _blockCacheService = blockCacheService;
            _processingQueue = processingQueue;
            _mergeSyncController = mergeSyncController;
            _logger = logManager.GetClassLogger();
        }

        public async Task<ResultWrapper<PayloadStatusV1>> HandleAsync(BlockRequestResult request)
        {
            string requestStr = $"a new payload: {request}";
            if (_logger.IsInfo) { _logger.Info($"Received {requestStr}"); }

            request.TryGetBlock(out Block? block);
            if (block == null)
            {
                if (_logger.IsWarn)
                    _logger.Warn($"Invalid block. Result of {requestStr}");

                return NewPayloadV1Result.Invalid(null, $"Block {request} could not be parsed as a block");
            }

            if (_blockValidator.ValidateHash(block.Header) == false)
            {
                if (_logger.IsWarn)
                    _logger.Warn($"InvalidBlockHash. Result of {requestStr}");

                return NewPayloadV1Result.InvalidBlockHash;
            }

            
            block.Header.TotalDifficulty = _poSSwitcher.FinalTotalDifficulty;
            BlockHeader? parentHeader = _blockTree.FindHeader(request.ParentHash, BlockTreeLookupOptions.None);
            bool parentExists = parentHeader != null;
            bool parentProcessed = parentExists && _blockTree.WasProcessed(parentHeader!.Number,
                parentHeader!.Hash ?? parentHeader.CalculateHash());
            if (!parentExists)
            {
                // possible that headers sync finished before this was called, so blocks in cache weren't inserted
                if (!_beaconSyncStrategy.IsBeaconSyncFinished(parentHeader))
                {
                    bool inserted = TryInsertDanglingBlock(block);
                    if (_logger.IsInfo)
                    {
                        if (inserted)
                            _logger.Info(
                                $"BeaconSync not finished - block {block} inserted");
                        else
                            _logger.Info(
                                $"BeaconSync not finished - block {block} accepted");
                    }

                    return inserted ? NewPayloadV1Result.Syncing : NewPayloadV1Result.Accepted;
                }

                if (_logger.IsInfo) _logger.Info($"Insert block into cache without parent {block}");
                _blockCacheService.BlockCache.TryAdd(request.BlockHash, block);
                return NewPayloadV1Result.Accepted;
            }

            // we need to check if the head is greater than block.Number. In fast sync we could return Valid to CL without this if
            if (block.Number <= (_blockTree.Head?.Number ?? 0))
            {
                bool canIgnoreNewPayload = _blockTree.IsMainChain(block.Header);
                if (canIgnoreNewPayload)
                {
                    if (_logger.IsInfo) _logger.Info($"Valid... A new payload ignored. Block {block.ToString(Block.Format.FullHashAndNumber)} found in main chain.");
                    return NewPayloadV1Result.Valid(block.Hash);
                }
            }

            if (!parentProcessed)
            {
                BlockTreeInsertOptions insertOptions = BlockTreeInsertOptions.BeaconBlockInsert;
                _blockTree.Insert(block, true, insertOptions);
                if (_logger.IsInfo) _logger.Info("Syncing... Parent wasn't processed. Inserting block.");
                return NewPayloadV1Result.Syncing;
            }

            if (_poSSwitcher.TerminalTotalDifficulty == null ||
                parentHeader!.TotalDifficulty < _poSSwitcher.TerminalTotalDifficulty)
            {
                if (_logger.IsWarn)
                    _logger.Warn(
                        $"Invalid terminal block. Nethermind TTD {_poSSwitcher.TerminalTotalDifficulty}, Parent TD: {parentHeader!.TotalDifficulty}. Request: {requestStr}");

                // https://github.com/ethereum/execution-apis/blob/main/src/engine/specification.md#specification
                // {status: INVALID, latestValidHash: 0x0000000000000000000000000000000000000000000000000000000000000000, validationError: errorMessage | null} if terminal block conditions are not satisfied
                return NewPayloadV1Result.Invalid(Keccak.Zero);
            }

            _mergeSyncController.StopSyncing();
            (ValidationResult ValidationResult, string? Message) result =
                ValidateBlockAndProcess(block, out Block? processedBlock, parentHeader);
            if ((result.ValidationResult & ValidationResult.AlreadyKnown) != 0 ||
                result.ValidationResult == ValidationResult.Invalid)
            {
                bool isValid = (result.ValidationResult & ValidationResult.Valid) != 0;
                if (_logger.IsInfo)
                {
                    string resultStr = isValid ? "Valid" : "Invalid";
                    if (_logger.IsInfo) _logger.Info($"{resultStr}. Result of {requestStr}");
                }

                return ResultWrapper<PayloadStatusV1>.Success(BuildExecutePayloadResult(request, isValid, parentHeader,
                    result.Message));
            }

            if (result.ValidationResult == ValidationResult.Syncing)
            {
                if (_logger.IsInfo) _logger.Info($"Processing queue wasn't empty added to queue {requestStr}");
                return NewPayloadV1Result.Syncing;
            }

            if (processedBlock == null)
            {
                if (_logger.IsInfo) { _logger.Info($"Invalid block processed. Result of {requestStr}"); }

                return ResultWrapper<PayloadStatusV1>.Success(BuildExecutePayloadResult(request, false, parentHeader,
                    $"Processed block is null, request {request}"));
            }

            if (_logger.IsInfo)
                _logger.Info($"Valid. Result of {requestStr}");

            return NewPayloadV1Result.Valid(request.BlockHash);
        }

        private (ValidationResult ValidationResult, string? Message) ValidateBlockAndProcess(Block block,
            out Block? processedBlock, BlockHeader parent)
        {
            string? validationMessage = null;
            processedBlock = null;

            bool isRecentBlock = _latestBlocks.TryGet(block.Hash!, out bool isValid);
            if (isRecentBlock)
            {
                if (isValid == false && _logger.IsWarn)
                {
                    validationMessage = $"Invalid block {block} sent from latestBlock cache";
                    if (_logger.IsWarn) _logger.Warn(validationMessage);
                }

                return (ValidationResult.AlreadyKnown |
                        (isValid ? ValidationResult.Valid : ValidationResult.Invalid), validationMessage);
            }
            else
            {
                bool validAndProcessed = ValidateWithBlockValidator(block, parent, out processedBlock);
                if (validAndProcessed)
                {
                    if (_processingQueue.IsEmpty)
                    {
                        // processingQueue is empty so we can process the block in synchronous way
                        _blockTree.SuggestBlock(block, BlockTreeSuggestOptions.None, false);
                        processedBlock = _processor.Process(block, GetProcessingOptions(), NullBlockTracer.Instance);
                    }
                    else
                    {
                        // this is needed for restarts. We have blocks in queue so we should add it to queue and return SYNCING
                        _blockTree.SuggestBlock(block, BlockTreeSuggestOptions.ShouldProcess);
                        return (ValidationResult.Syncing, null);
                    }
            
                    if (processedBlock == null)
                    {
                        if (_logger.IsWarn)
                        {
                            _logger.Warn(
                                $"Block {block.ToString(Block.Format.FullHashAndNumber)} cannot be processed and wont be accepted to the tree.");
                        }

                        validAndProcessed = false;
                    }
                }

                _latestBlocks.Set(block.Hash!, validAndProcessed);
                return (validAndProcessed ? ValidationResult.Valid : ValidationResult.Invalid, validationMessage);
            }
        }

        private bool ValidateWithBlockValidator(Block block, BlockHeader parent, out Block? processedBlock)
        {
            block.Header.TotalDifficulty = parent.TotalDifficulty + block.Difficulty;
            processedBlock = null;
            block.Header.IsPostMerge = true;
            bool isValid = _blockValidator.ValidateSuggestedBlock(block);
            if (!isValid)
            {
                if (_logger.IsWarn)
                {
                    _logger.Warn(
                        $"Block validator rejected the block {block.ToString(Block.Format.FullHashAndNumber)}");
                }
            }

            return isValid;
        }

        private ProcessingOptions GetProcessingOptions()
        {
            ProcessingOptions options = ProcessingOptions.EthereumMerge;
            if (_initConfig.StoreReceipts)
            {
                options |= ProcessingOptions.StoreReceipts;
            }

            return options;
        }

        private PayloadStatusV1 BuildExecutePayloadResult(BlockRequestResult request, bool isValid, BlockHeader? parent,
            string? validationMessage)
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
                if (_lastValidHashes.ContainsKey(request.ParentHash))
                {
                    if (_lastValidHashes.TryRemove(request.ParentHash, out Keccak? lastValidHash))
                    {
                        _lastValidHashes.TryAdd(request.BlockHash, lastValidHash);
                    }

                    payloadStatus.LatestValidHash = lastValidHash;
                }
                else
                {
                    if (parent != null)
                    {
                        _lastValidHashes.TryAdd(request.BlockHash, request.ParentHash);
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
