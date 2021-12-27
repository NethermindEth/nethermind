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
using System.Linq;
using System.Threading;
using Nethermind.Api;
using Nethermind.Blockchain;
using Nethermind.Consensus;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Evm.Tracing;
using Nethermind.Facade.Eth;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Data;
using Nethermind.State;

namespace Nethermind.Merge.Plugin.Handlers
{
    /// <summary>
    /// https://hackmd.io/@n0ble/consensus_api_design_space
    /// Verifies the payload according to the execution environment rule set (EIP-3675) and returns the status of the verification
    /// </summary>
    public class ExecutePayloadHandler : IHandler<BlockRequestResult, ExecutePayloadResult>
    {
        private const string AndWontBeAcceptedToTheTree = "and wont be accepted to the tree";
        private readonly IHeaderValidator _headerValidator;
        private readonly IBlockTree _blockTree;
        private readonly IBlockchainProcessor _processor;
        private readonly IEthSyncingInfo _ethSyncingInfo;
        private readonly IInitConfig _initConfig;
        private readonly ILogger _logger;
        private SemaphoreSlim _blockValidationSemaphore;
        private readonly LruCache<Keccak, bool> _latestBlocks = new(50, "LatestBlocks");

        public ExecutePayloadHandler(
            IHeaderValidator headerValidator,
            IBlockTree blockTree,
            IBlockchainProcessor processor,
            IEthSyncingInfo ethSyncingInfo,
            IInitConfig initConfig,
            ILogManager logManager)
        {
            _headerValidator = headerValidator ?? throw new ArgumentNullException(nameof(headerValidator));
            _blockTree = blockTree;
            _processor = processor;
            _ethSyncingInfo = ethSyncingInfo;
            _initConfig = initConfig;
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

        public ResultWrapper<ExecutePayloadResult> Handle(BlockRequestResult request)
        {
            ExecutePayloadResult executePayloadResult = new() {BlockHash = request.BlockHash};

            // uncomment when Syncing implementation will be ready
            // if (_ethSyncingInfo.IsSyncing())
            // {
            //     executePayloadResult.Status = VerificationStatus.Syncing;
            //     return ResultWrapper<ExecutePayloadResult>.Success(executePayloadResult);
            // }

            ValidationResult result = ValidateRequestAndProcess(request, out Block? processedBlock);
            if ((result & ValidationResult.AlreadyKnown) != 0 || result == ValidationResult.Invalid)
            {
                bool isValid = (result & ValidationResult.Valid) != 0;
                executePayloadResult.EnumStatus = isValid ? VerificationStatus.Valid : VerificationStatus.Invalid;
                return ResultWrapper<ExecutePayloadResult>.Success(executePayloadResult);
            }
            else if (processedBlock == null)
            {
                executePayloadResult.EnumStatus = VerificationStatus.Invalid;
                return ResultWrapper<ExecutePayloadResult>.Success(executePayloadResult);
            }

            _blockTree.SuggestBlock(processedBlock);
            executePayloadResult.EnumStatus = VerificationStatus.Valid;
            // _blockValidationSemaphore.Wait();
            return ResultWrapper<ExecutePayloadResult>.Success(executePayloadResult);
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
            ProcessingOptions options = ProcessingOptions.ExecutePayloadValidation;
            if (_initConfig.StoreReceipts)
            {
                options |= ProcessingOptions.StoreReceipts;
            }

            return options;
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
