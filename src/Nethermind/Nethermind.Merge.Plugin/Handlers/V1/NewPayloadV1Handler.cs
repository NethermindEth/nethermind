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
        private readonly IEthSyncingInfo _ethSyncingInfo;
        private readonly IInitConfig _initConfig;
        private readonly IPoSSwitcher _poSSwitcher;
        private readonly IBeaconSyncStrategy _beaconSyncStrategy;
        private readonly IBeaconPivot _beaconPivot;
        private readonly IBlockCacheService _blockCacheService;
        private readonly ISyncProgressResolver _syncProgressResolver;
        private readonly IBlockProcessingQueue _blockProcessingQueue;
        private readonly ILogger _logger;
        private readonly LruCache<Keccak, bool> _latestBlocks = new(50, "LatestBlocks");
        private readonly ConcurrentDictionary<Keccak, Keccak> _lastValidHashes = new();
        private long _state = 0;

        public NewPayloadV1Handler(
            IBlockValidator blockValidator,
            IBlockTree blockTree,
            IBlockchainProcessor processor,
            IEthSyncingInfo ethSyncingInfo,
            IInitConfig initConfig,
            IPoSSwitcher poSSwitcher,
            IBeaconSyncStrategy beaconSyncStrategy,
            IBeaconPivot beaconPivot,
            IBlockCacheService blockCacheService,
            ISyncProgressResolver syncProgressResolver,
            IBlockProcessingQueue blockProcessingQueue,
            ILogManager logManager)
        {
            _blockValidator = blockValidator ?? throw new ArgumentNullException(nameof(blockValidator));
            _blockTree = blockTree;
            _processor = processor;
            _ethSyncingInfo = ethSyncingInfo;
            _initConfig = initConfig;
            _poSSwitcher = poSSwitcher;
            _beaconSyncStrategy = beaconSyncStrategy;
            _beaconPivot = beaconPivot;
            _blockCacheService = blockCacheService;
            _syncProgressResolver = syncProgressResolver;
            _blockProcessingQueue = blockProcessingQueue;
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
            
            if (!_beaconSyncStrategy.IsBeaconSyncFinished())
            {
                if (_blockCacheService.BlockCache.ContainsKey(block.Hash))
                {
                    return NewPayloadV1Result.Syncing;
                }

                BlockTreeInsertOptions insertOptions = BlockTreeInsertOptions.SkipUpdateBestPointers |
                                                       BlockTreeInsertOptions.TotalDifficultyNotNeeded |
                                                       BlockTreeInsertOptions.NotOnMainChain;

                if (!_blockTree.IsKnownBlock(block.Number, block.Hash))
                {
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
                            if (current.Hash == _beaconPivot.PivotHash)
                            {
                                break;
                            }

                            _blockCacheService.BlockCache.TryGetValue(current.ParentHash, out Block? parentBlock);
                            current = parentBlock;
                        }

                        if (current == null)
                        {
                            _blockCacheService.BlockCache.TryAdd(request.BlockHash, block);
                            return NewPayloadV1Result.Accepted;
                        }

                        while (stack.TryPop(out Block? child))
                        {
                            _blockTree.Insert(child, true, insertOptions);
                        }
                    }
                    
                    _blockCacheService.ProcessDestination = block.Hash;
                    if (!_beaconSyncStrategy.IsBeaconSyncHeadersFinished())
                    {
                        return NewPayloadV1Result.Syncing;
                    }
                }
            }

            BlockHeader? parentHeader = _blockTree.FindHeader(request.ParentHash, BlockTreeLookupOptions.None);
            if (parentHeader == null)
            {
                _logger.Info($"Insert block into cache without parent {block}");
                _blockCacheService.BlockCache.TryAdd(request.BlockHash, block);
                return NewPayloadV1Result.Accepted;
            }

            if (!_beaconSyncStrategy.IsBeaconSyncFinished())
            {
                if (_beaconSyncStrategy.IsBeaconSyncHeadersFinished())
                {
                    parentHeader.TotalDifficulty = _blockTree.BackFillTotalDifficulty(_beaconPivot.PivotNumber, block.Number - 1);
                }
                
                if (parentHeader.TotalDifficulty == 0)
                {
                    parentHeader.TotalDifficulty = _blockTree.BackFillTotalDifficulty(_beaconPivot.PivotNumber, block.Number - 1);
                }
                
                // TODO: beaconsync add TDD and validation checks
                block.Header.TotalDifficulty = parentHeader.TotalDifficulty + block.Difficulty;
                block.Header.IsPostMerge = true;
                if (_beaconSyncStrategy.FastSyncEnabled)
                {
                    bool parentProcessed = _blockTree.WasProcessed(parentHeader.Number, parentHeader.Hash);
                    if (!parentProcessed)
                    {
                        long state = _syncProgressResolver.FindBestFullState(); // ToDo Sarah: I think we need to findBestFullState only one time because isFastSyncTransition condition
                        if (state > 0)
                        {
                            bool shouldProcess = block.Number > state;
                            if (shouldProcess)
                            {
                                Stack<Block> stack = new();
                                Block? current = block;
                                // re-insert block as header is encoded with TD 0
                                _blockTree.Insert(block); //ToDo Sarah: can't we do operation similar to SetTotalDifficulty in blockTree instead of reinserting blocks?
                                BlockHeader parent = parentHeader;

                                while (current.Number > state)
                                {
                                    if (_blockTree.WasProcessed(current.Number, current.Hash))
                                    {
                                        break;
                                    }
                                    
                                    stack.Push(current);
                                    current = _blockTree.FindBlock(parent.Hash,
                                        BlockTreeLookupOptions.TotalDifficultyNotNeeded);
                                    parent = _blockTree.FindHeader(current.ParentHash);
                                    current.Header.TotalDifficulty = parent.TotalDifficulty + current.Difficulty;
                                    // re-insert block as header is originally encoded with TD 0
                                    _blockTree.Insert(current);
                                }

                                while (stack.TryPop(out Block child))
                                {
                                    _blockTree.SuggestBlock(child);
                                }
                            }
                        }

                        return NewPayloadV1Result.Syncing;
                    }
                }
                else
                {
                    bool wasProcessed = _blockTree.WasProcessed(parentHeader.Number, parentHeader.Hash ?? parentHeader.CalculateHash());
                    if (!wasProcessed)
                    {
                        bool parentPivotProcessed = _beaconPivot.IsPivotParentProcessed();
                        if (parentPivotProcessed)
                        {
                            // ToDo add beaconSync validation
                            _logger.Info(
                                $"Parent pivot was processed. Pivot: {_beaconPivot.PivotNumber} {_beaconPivot.PivotHash} Suggesting block {block}");
                            _blockTree.SuggestBlock(block);
                        }
                        else
                        {
                            _logger.Info($"Inserted {block}");
                            _blockTree.Insert(block, true);
                        }
                        
                        return NewPayloadV1Result.Syncing;
                    }
                }
                
                _blockCacheService.BlockCache.Clear();
            }

            if (_poSSwitcher.TerminalTotalDifficulty == null ||
                parentHeader.TotalDifficulty < _poSSwitcher.TerminalTotalDifficulty)
            {
                if (_logger.IsWarn)
                    _logger.Warn(
                        $"Invalid terminal block. Nethermind TTD {_poSSwitcher.TerminalTotalDifficulty}, Parent TD: {parentHeader.TotalDifficulty}. Request: {requestStr}");

                return NewPayloadV1Result.InvalidTerminalBlock;
            }

            (ValidationResult ValidationResult, string? Message) result =
                ValidateBlockAndProcess(block, out Block? processedBlock, parentHeader);
            if ((result.ValidationResult & ValidationResult.AlreadyKnown) != 0 ||
                result.ValidationResult == ValidationResult.Invalid)
            {
                bool isValid = (result.ValidationResult & ValidationResult.Valid) != 0;
                if (_logger.IsInfo)
                {
                    string resultStr = isValid ? "Valid" : "Invalid";
                    _logger.Info($"{resultStr}. Result of {requestStr}");
                }

                return ResultWrapper<PayloadStatusV1>.Success(BuildExecutePayloadResult(request, isValid, parentHeader,
                    result.Message));
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
                    _logger.Warn(validationMessage);
                }

                return (ValidationResult.AlreadyKnown |
                        (isValid ? ValidationResult.Valid : ValidationResult.Invalid), validationMessage);
            }
            else
            {
                // during the fast sync we could find the header on canonical chain which means that this header is valid
                if (_blockTree.IsMainChain(block.Header))
                {
                    return (ValidationResult.Valid | ValidationResult.AlreadyKnown, validationMessage);
                }

                processedBlock = _blockTree.FindBlock(block.Hash!, BlockTreeLookupOptions.None);
                if (processedBlock != null && _blockTree.WasProcessed(processedBlock.Number, processedBlock.Hash))
                {
                    return (ValidationResult.Valid | ValidationResult.AlreadyKnown, validationMessage);
                }

                bool validAndProcessed = ValidateAndProcess(block, parent!, out processedBlock);

                _latestBlocks.Set(block.Hash!, validAndProcessed);
                return (validAndProcessed ? ValidationResult.Valid : ValidationResult.Invalid, validationMessage);
            }
        }

        private bool ValidateAndProcess(Block block, BlockHeader parent, out Block? processedBlock)
        {
            block.Header.TotalDifficulty = parent.TotalDifficulty + block.Difficulty;
            processedBlock = null;
            block.Header.IsPostMerge = true;
            if (_blockValidator.ValidateSuggestedBlock(block) == false)
            {
                if (_logger.IsWarn)
                {
                    _logger.Warn(
                        $"Block validator rejected the block {block.ToString(Block.Format.FullHashAndNumber)}");
                }

                return false;
            }
            
            _blockTree.SuggestBlock(block, BlockTreeSuggestOptions.None, false);
            processedBlock = _processor.Process(block, GetProcessingOptions(), NullBlockTracer.Instance);
            if (processedBlock == null)
            {
                if (_logger.IsWarn)
                {
                    _logger.Warn(
                        $"Block {block.ToString(Block.Format.FullHashAndNumber)} cannot be processed and wont be accepted to the tree.");
                }

                return false;
            }

            return true;
        }

        private ProcessingOptions GetProcessingOptions()
        {
            ProcessingOptions options = ProcessingOptions.None;
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

        private bool IsParentProcessed(BlockHeader blockHeader)
        {
            BlockHeader? parent =
                _blockTree.FindParentHeader(blockHeader, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
            if (parent != null)
            {
                return _blockTree.WasProcessed(parent.Number, parent.Hash ?? parent.CalculateHash());
            }

            return false;
        }

        [Flags]
        private enum ValidationResult
        {
            Invalid = 0,
            Valid = 1,
            AlreadyKnown = 2
        }
    }
}
