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
using Nethermind.Evm.Tracing;
using Nethermind.Facade.Eth;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.Data.V1;
using Nethermind.Synchronization;

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
        private readonly ISynchronizer _synchronizer;
        private readonly ISyncConfig _syncConfig;
        private readonly ILogger _logger;
        private readonly LruCache<Keccak, bool> _latestBlocks = new(50, "LatestBlocks");
        private readonly ConcurrentDictionary<Keccak, Keccak> _lastValidHashes = new();
        private bool synced = false;

        public NewPayloadV1Handler(
            IBlockValidator blockValidator,
            IBlockTree blockTree,
            IBlockchainProcessor processor,
            IEthSyncingInfo ethSyncingInfo,
            IInitConfig initConfig,
            IPoSSwitcher poSSwitcher,
            ISynchronizer synchronizer,
            ISyncConfig syncConfig,
            ILogManager logManager)
        {
            _blockValidator = blockValidator ?? throw new ArgumentNullException(nameof(blockValidator));
            _blockTree = blockTree;
            _processor = processor;
            _ethSyncingInfo = ethSyncingInfo;
            _initConfig = initConfig;
            _poSSwitcher = poSSwitcher;
            _synchronizer = synchronizer;
            _syncConfig = syncConfig;
            _logger = logManager.GetClassLogger();
        }

        public async Task<ResultWrapper<PayloadStatusV1>> HandleAsync(BlockRequestResult request)
        {
            if (_logger.IsInfo)
            {
                _logger.Info($"Received payload {request}");
            }
            request.TryGetBlock(out Block? block);
            if (block == null)
            { 
                if (_logger.IsWarn)
                {
                    _logger.Warn($"Result of payload: Invalid block");
                }
                return NewPayloadV1Result.Invalid(null, $"Block {request} could not be parsed as a block");
            }

            if (_blockValidator.ValidateHash(block.Header) == false)
            { 
                if (_logger.IsWarn)
                {
                    _logger.Warn($"Result of payload: Invalid block hash {block.Header}");
                }
                return NewPayloadV1Result.InvalidBlockHash;
            }

            // ToDo wait for final PostMerge sync
            if (_syncConfig.FastSync && _blockTree.LowestInsertedBodyNumber != 0)
            { 
                if (_logger.IsInfo)
                {
                    _logger.Info($"Result of payload: Syncing {block}");
                }
                return NewPayloadV1Result.Syncing;
            }

            Block? parent = _blockTree.FindBlock(request.ParentHash, BlockTreeLookupOptions.None);
            if (parent == null)
            { 
                if (_logger.IsInfo)
                {
                    _logger.Info($"Result of payload: Accepted {block}");
                }
                // ToDo wait for final PostMerge sync
                return NewPayloadV1Result.Accepted;
            }

            await _synchronizer.StopAsync();
            BlockHeader? parentHeader = parent.Header;
            // if (_ethSyncingInfo.IsSyncing() && synced == false)
            // {
            //      return NewPayloadV1Result.Syncing;
            // }
            // else if (synced == false)
            // {
            //     await _synchronizer.StopAsync();
            //     synced = true;
            // }
            //
            if (_poSSwitcher.TerminalTotalDifficulty == null ||
                parentHeader.TotalDifficulty < _poSSwitcher.TerminalTotalDifficulty)
            { 
                if (_logger.IsWarn)
                {
                    _logger.Warn($"Result of payload: Invalid Terminal Block {block}");
                }
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
                    _logger.Info($"Result of payload: Success {block}");
                }
                return ResultWrapper<PayloadStatusV1>.Success(BuildExecutePayloadResult(request, isValid, parentHeader,
                    result.Message));
            }

            if (processedBlock == null)
            { 
                if (_logger.IsInfo)
                {
                    _logger.Info($"Result of payload: Success {block}");
                }
                return ResultWrapper<PayloadStatusV1>.Success(BuildExecutePayloadResult(request, false, parentHeader,
                    $"Processed block is null, request {request}"));
            }
             
            if (_logger.IsInfo)
            {
                _logger.Info($"Result of payload: Valid {block}");
            }
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
                if (processedBlock != null)
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
            
            var addResult = _blockTree.SuggestBlock(block, false, false);
            _logger.Info($"{processedBlock} add result {addResult}");
            
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

        [Flags]
        private enum ValidationResult
        {
            Invalid = 0,
            Valid = 1,
            AlreadyKnown = 2
        }
    }
}
