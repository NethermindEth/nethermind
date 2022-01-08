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
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Consensus;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Db;
using Nethermind.Evm.Tracing;
using Nethermind.Facade.Eth;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.Data.V1;
using Nethermind.Synchronization;

namespace Nethermind.Merge.Plugin.Handlers
{
    /// <summary>
    /// https://hackmd.io/@n0ble/kintsugi-spec
    /// Verifies the payload according to the execution environment rule set (EIP-3675)
    /// and returns the status of the verification and the hash of the last valid block
    /// </summary>
    public class ExecutePayloadV1Handler : IAsyncHandler<BlockRequestResult, ExecutePayloadV1Result>
    {
        private readonly IBlockValidator _blockValidator;
        private readonly IBlockTree _blockTree;
        private readonly IBlockchainProcessor _processor;
        private readonly IEthSyncingInfo _ethSyncingInfo;
        private readonly IInitConfig _initConfig;
        private readonly IMergeConfig _mergeConfig;
        private readonly ISynchronizer _synchronizer;
        private readonly ISyncConfig _syncConfig;
        private readonly ILogger _logger;
        private SemaphoreSlim _blockValidationSemaphore;
        private readonly LruCache<Keccak, bool> _latestBlocks = new(50, "LatestBlocks");
        private readonly ConcurrentDictionary<Keccak, Keccak> _lastValidHashes = new ();
        private bool synced = false;

        public ExecutePayloadV1Handler(
            IBlockValidator blockValidator,
            IBlockTree blockTree,
            IBlockchainProcessor processor,
            IEthSyncingInfo ethSyncingInfo,
            IInitConfig initConfig,
            IMergeConfig mergeConfig,
            ISynchronizer synchronizer,
            ISyncConfig syncConfig,
            ILogManager logManager)
        {
            _blockValidator = blockValidator ?? throw new ArgumentNullException(nameof(blockValidator));
            _blockTree = blockTree;
            _processor = processor;
            _ethSyncingInfo = ethSyncingInfo;
            _initConfig = initConfig;
            _mergeConfig = mergeConfig;
            _synchronizer = synchronizer;
            _syncConfig = syncConfig;
            _logger = logManager.GetClassLogger();
            _blockValidationSemaphore = new SemaphoreSlim(0);
            _processor.BlockProcessed += (s, e) =>
            {
                _blockValidationSemaphore.Release(1);
            };
            _processor.BlockInvalid += (s, e) =>
            {
                _blockValidationSemaphore.Release(1);
            };
        }

        public async Task<ResultWrapper<ExecutePayloadV1Result>> HandleAsync(BlockRequestResult request)
        {
            ExecutePayloadV1Result executePayloadResult = new();

            // ToDo wait for final PostMerge sync
            if (_syncConfig.FastSync && _blockTree.LowestInsertedBodyNumber != 0)
            {
                executePayloadResult.Status = Status.Syncing;
                return ResultWrapper<ExecutePayloadV1Result>.Success(executePayloadResult);
            }
            Block? parent = _blockTree.FindBlock(request.ParentHash, BlockTreeLookupOptions.None);
            if (parent == null)
            {
                // ToDo wait for final PostMerge sync
                executePayloadResult.Status = Status.Syncing;
                return ResultWrapper<ExecutePayloadV1Result>.Success(executePayloadResult);
            }
            
            BlockHeader? parentHeader = parent.Header;
            if (_ethSyncingInfo.IsSyncing() && synced == false)
            {
                executePayloadResult.Status = Status.Syncing;
                return ResultWrapper<ExecutePayloadV1Result>.Success(executePayloadResult);
            }
            else if (synced == false)
            {
                await _synchronizer.StopAsync();
                synced = true;
            }


            if (parentHeader.TotalDifficulty < _mergeConfig.TerminalTotalDifficulty)
            {
                ResultWrapper<ExecutePayloadV1Result>.Fail($"Invalid total difficulty: {parentHeader.TotalDifficulty} for block header: {parentHeader}", MergeErrorCodes.InvalidTerminalBlock);
            }
            
            (ValidationResult ValidationResult, string? Message) result = ValidateRequestAndProcess(request, out Block? processedBlock, parentHeader);
            if ((result.ValidationResult & ValidationResult.AlreadyKnown) != 0 || result.ValidationResult == ValidationResult.Invalid)
            {
                bool isValid = (result.ValidationResult & ValidationResult.Valid) !=   0;
                return ResultWrapper<ExecutePayloadV1Result>.Success(BuildExecutePayloadResult(request, isValid, parentHeader, result.Message));
            }

            if (processedBlock == null)
            {
                return ResultWrapper<ExecutePayloadV1Result>.Success(BuildExecutePayloadResult(request, false, parentHeader, $"Processed block is null, request {request}"));
            }

            processedBlock.Header.IsPostMerge = true;
            _blockTree.SuggestBlock(processedBlock, true);
            executePayloadResult.Status = Status.Valid;
            executePayloadResult.LatestValidHash = request.BlockHash;
            _blockValidationSemaphore.Wait();
            return ResultWrapper<ExecutePayloadV1Result>.Success(executePayloadResult);
        }

        private (ValidationResult ValidationResult, string? Message) ValidateRequestAndProcess(BlockRequestResult request, out Block? processedBlock, BlockHeader parent)
        {
            string? validationMessage = null;
            processedBlock = null;

            if (request.TryGetBlock(out Block? block) && block != null)
            {
                if (_blockValidator.ValidateHash(block.Header) == false)
                {
                    validationMessage = $"Invalid block hash {block.Header}";
                    return (ValidationResult.Invalid, validationMessage);
                }
                bool isRecentBlock = _latestBlocks.TryGet(request.BlockHash, out bool isValid);
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
                    BlockHeader? headerOnCanonicalChain = _blockTree.FindHeader(request.BlockHash, BlockTreeLookupOptions.RequireCanonical);
                    if (headerOnCanonicalChain != null)
                    {
                        return (ValidationResult.Valid | ValidationResult.AlreadyKnown, validationMessage);
                    }
                    
                    processedBlock = _blockTree.FindBlock(request.BlockHash, BlockTreeLookupOptions.None);
                    if (processedBlock != null)
                    {
                        return (ValidationResult.Valid | ValidationResult.AlreadyKnown, validationMessage);
                    }

                    bool validAndProcessed = ValidateAndProcess(block, parent!, out processedBlock);

                    _latestBlocks.Set(request.BlockHash, validAndProcessed);
                    return (validAndProcessed ? ValidationResult.Valid : ValidationResult.Invalid, validationMessage);
                }
            }
            else
            {
                if (_logger.IsWarn)
                    _logger.Warn($"Block {request} could not be parsed as block and wont be accepted to the tree.");
            }

            return (ValidationResult.Invalid, validationMessage);
        }
        
        private bool ValidateAndProcess(Block block, BlockHeader parent, out Block? processedBlock)
        {
            block.Header.TotalDifficulty = parent.TotalDifficulty + block.Difficulty;
            processedBlock = null;
                if (_blockValidator.ValidateSuggestedBlock(block) == false)
                {
                    if (_logger.IsWarn)
                    {
                        _logger.Warn(
                            $"Block validator rejected the block {block.ToString(Block.Format.FullHashAndNumber)}");
                    }
                    return false;
                }
            
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
            ProcessingOptions options = ProcessingOptions.EthereumMerge;
            if (_initConfig.StoreReceipts)
            {
                options |= ProcessingOptions.StoreReceipts;
            }

            return options;
        }

        private ExecutePayloadV1Result BuildExecutePayloadResult(BlockRequestResult request, bool isValid, BlockHeader? parent, string? validationMessage)
        {
            ExecutePayloadV1Result executePayloadResult = new();
            if (isValid)
            {
                executePayloadResult.Status = Status.Valid;
                executePayloadResult.LatestValidHash = request.BlockHash;
            }
            else
            {
                executePayloadResult.ValidationError = validationMessage;
                executePayloadResult.Status = Status.Invalid;
                if (_lastValidHashes.ContainsKey(request.ParentHash))
                {
                    if (_lastValidHashes.TryRemove(request.ParentHash, out Keccak? lastValidHash))
                    {
                        _lastValidHashes.TryAdd(request.BlockHash, lastValidHash);   
                    }

                    executePayloadResult.LatestValidHash = lastValidHash;
                }
                else
                {
                    if (parent != null)
                    {
                        _lastValidHashes.TryAdd(request.BlockHash, request.ParentHash);
                        executePayloadResult.LatestValidHash = request.ParentHash;
                    }
                    else
                    {
                        executePayloadResult.LatestValidHash = _blockTree.HeadHash;
                    }
                }

            }

            return executePayloadResult;
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
