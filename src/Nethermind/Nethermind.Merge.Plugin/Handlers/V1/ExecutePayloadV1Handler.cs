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
using Nethermind.Api;
using Nethermind.Blockchain;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm.Tracing;
using Nethermind.Facade.Eth;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Data;

namespace Nethermind.Merge.Plugin.Handlers
{
    /// <summary>
    /// https://hackmd.io/@n0ble/kintsugi-spec
    /// Verifies the payload according to the execution environment rule set (EIP-3675)
    /// and returns the status of the verification and the hash of the last valid block
    /// </summary>
    public class ExecutePayloadV1Handler : IHandler<BlockRequestResult, ExecutePayloadV1Result>
    {
        private const string AndWontBeAcceptedToTheTree = "and wont be accepted to the tree";
        private readonly IHeaderValidator _headerValidator;
        private readonly IBlockTree _blockTree;
        private readonly IBlockchainProcessor _processor;
        private readonly ISpecProvider _specProvider;
        private readonly IBlockPreprocessorStep _preprocessor;
        private readonly IEthSyncingInfo _ethSyncingInfo;
        private readonly IInitConfig _initConfig;
        private readonly ILogger _logger;
        private SemaphoreSlim _blockValidationSemaphore;
        private readonly LruCache<Keccak, bool> _latestBlocks = new(50, "LatestBlocks");
        private readonly ConcurrentDictionary<Keccak, Keccak> _lastValidHashes = new ();

        public ExecutePayloadV1Handler(
            IHeaderValidator headerValidator,
            IBlockTree blockTree,
            IBlockchainProcessor processor,
            IEthSyncingInfo ethSyncingInfo,
            IInitConfig initConfig,
            ISpecProvider specProvider, 
            IBlockPreprocessorStep preprocessor,
            ILogManager logManager)
        {
            _headerValidator = headerValidator ?? throw new ArgumentNullException(nameof(headerValidator));
            _blockTree = blockTree;
            _processor = processor;
            _ethSyncingInfo = ethSyncingInfo;
            _initConfig = initConfig;
            _specProvider = specProvider;
            _preprocessor = preprocessor;
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

        public ResultWrapper<ExecutePayloadV1Result> Handle(BlockRequestResult request)
        {
            ExecutePayloadV1Result executePayloadResult = new();

            // if (_ethSyncingInfo.IsSyncing())
            // {
            //     executePayloadResult.EnumStatus = VerificationStatus.Syncing;
            //     return ResultWrapper<ExecutePayloadV1Result>.Success(executePayloadResult);
            // }

            ValidationResult result = ValidateRequestAndProcess(request, out Block? processedBlock);
            if ((result & ValidationResult.AlreadyKnown) != 0 || result == ValidationResult.Invalid)
            {
                bool isValid = (result & ValidationResult.Valid) != 0;
                return ResultWrapper<ExecutePayloadV1Result>.Success(BuildExecutePayloadResult(request, isValid));
            }

            if (processedBlock == null)
            {
                return ResultWrapper<ExecutePayloadV1Result>.Success(BuildExecutePayloadResult(request, false));
            }

            _blockTree.SuggestBlock(processedBlock);
            executePayloadResult.EnumStatus = VerificationStatus.Valid;
            executePayloadResult.LatestValidHash = request.BlockHash;
            _blockValidationSemaphore.Wait();
            return ResultWrapper<ExecutePayloadV1Result>.Success(executePayloadResult);
        }

        private ValidationResult ValidateRequestAndProcess(BlockRequestResult request, out Block? processedBlock)
        {
            processedBlock = null;

            if (request.TryGetBlock(out Block? block) && block != null)
            {
                bool isRecentBlock = _latestBlocks.TryGet(request.BlockHash, out bool isValid);
                if (isRecentBlock)
                {
                    return ValidationResult.AlreadyKnown |
                           (isValid ? ValidationResult.Valid : ValidationResult.Invalid);
                }
                else
                {
                    processedBlock = _blockTree.FindBlock(request.BlockHash, BlockTreeLookupOptions.None);

                    if (processedBlock != null)
                    {
                        return ValidationResult.Valid | ValidationResult.AlreadyKnown;
                    }

                    bool validAndProcessed =
                        TryGetParent(request.ParentHash, out BlockHeader? parent)
                        && ValidateAndProcess(block, parent!, out processedBlock);


                    _latestBlocks.Set(request.BlockHash, validAndProcessed);
                    return validAndProcessed ? ValidationResult.Valid : ValidationResult.Invalid;
                }
            }
            else
            {
                if (_logger.IsWarn)
                    _logger.Warn($"Block {request} could not be parsed as block {AndWontBeAcceptedToTheTree}.");
            }

            return ValidationResult.Invalid;
        }

        private bool TryGetParent(Keccak? parentHash, out BlockHeader? parent)
        {
            if (parentHash == null)
            {
                parent = null;
                return false;
            }
            else
            {
                parent = _blockTree.FindHeader(parentHash);
                return parent != null;
            }
        }

        private bool ValidateAndProcess(Block block, BlockHeader parent, out Block? processedBlock)
        {
            block.Header.TotalDifficulty = parent.TotalDifficulty + block.Difficulty;
            processedBlock = null;
            if (_headerValidator.Validate(block.Header) == false)
            {
                return false;
            }

            processedBlock = _processor.Process(block, GetProcessingOptions(), NullBlockTracer.Instance);
            if (processedBlock == null)
            {
                if (_logger.IsWarn)
                {
                    _logger.Warn(
                        $"Block {block.ToString(Block.Format.FullHashAndNumber)} cannot be processed {AndWontBeAcceptedToTheTree}.");
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

        private ExecutePayloadV1Result BuildExecutePayloadResult(BlockRequestResult request, bool isValid)
        {
            ExecutePayloadV1Result executePayloadResult = new();
            if (isValid)
            {
                executePayloadResult.EnumStatus = VerificationStatus.Valid;
                executePayloadResult.LatestValidHash = request.BlockHash;
            }
            else
            {
                executePayloadResult.EnumStatus = VerificationStatus.Invalid;
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
                    if (TryGetParent(request.ParentHash, out _))
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
