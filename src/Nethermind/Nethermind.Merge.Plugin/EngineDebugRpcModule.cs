// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.Find;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Facade;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Data;
using Nethermind.JsonRpc.Modules.DebugModule;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Data;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nethermind.Merge.Plugin
{
    public class EngineDebugRpcModule : IEngineDebugRpcModule
    {
        private readonly IEngineDebugBridge _mergeDebugBridge;
        private readonly ILogManager _logManager;
        private readonly IJsonRpcConfig _jsonRpcConfig;
        private readonly ISpecProvider _specProvider;
        private readonly IBlockchainBridge _blockchainBridge;
        private readonly IBlocksConfig _blocksConfig;
        private readonly IBlockFinder _blockFinder;

        public EngineDebugRpcModule(
            ILogManager logManager,
            IEngineDebugBridge debugBridge,
            IJsonRpcConfig jsonRpcConfig,
            ISpecProvider specProvider,
            IBlockchainBridge blockchainBridge,
            IBlocksConfig blocksConfig,
            IBlockFinder blockFinder)
        {
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _mergeDebugBridge = debugBridge ?? throw new ArgumentNullException(nameof(debugBridge));
            _jsonRpcConfig = jsonRpcConfig ?? throw new ArgumentNullException(nameof(jsonRpcConfig));
            _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
            _blockchainBridge = blockchainBridge ?? throw new ArgumentNullException(nameof(blockchainBridge));
            _blocksConfig = blocksConfig ?? throw new ArgumentNullException(nameof(blocksConfig));
            _blockFinder = blockFinder ?? throw new ArgumentNullException(nameof(blockFinder));
        }

        public ResultWrapper<Hash256> debug_calculateBlockHash(ExecutionPayload executionPayload)
        {
            if (executionPayload == null)
            {
                return ResultWrapper<Hash256>.Fail("Execution payload cannot be null", ErrorCodes.InvalidRequest);
            }
            // Assuming we have a method to calculate the block hash from the execution payload
            BlockDecodingResult result = executionPayload.TryGetBlock();
            if(result.Block is not null)
            {
                Hash256 blockHash = result.Block.Header.GetOrCalculateHash();
                return ResultWrapper<Hash256>.Success(blockHash);
            }
            else
            {
                return ResultWrapper<Hash256>.Fail("Invalid execution payload", ErrorCodes.InvalidRequest);
            }
        }

        public ResultWrapper<ExecutionPayloadForDebugRpc> debug_generateNewPayload(BlockParameter blockParameter)
        {
            if (blockParameter == null)
            {
                return ResultWrapper<ExecutionPayloadForDebugRpc>.Fail("Block parameter cannot be null", ErrorCodes.InvalidRequest);
            }

            ExecutionPayloadForDebugRpc execPayloadForRpc = _mergeDebugBridge.GenerateNewPayload(blockParameter);

            if (execPayloadForRpc == null)
            {
                return ResultWrapper<ExecutionPayloadForDebugRpc>.Fail("Failed to generate new payload", ErrorCodes.InternalError);
            }
            return ResultWrapper<ExecutionPayloadForDebugRpc>.Success(execPayloadForRpc);
        }

        public ResultWrapper<ExecutionPayloadForDebugRpc> debug_generateNewPayload(BlockParameter blockParameterStart, BlockParameter blockParameterEnd)
        {
            throw new NotImplementedException();
        }

        public ResultWrapper<ExecutionPayloadForDebugRpc> debug_generateNewPayloadWithTransactions(TransactionForRpc[] transactions)
        {
            if (transactions == null || transactions.Length == 0)
            {
                return ResultWrapper<ExecutionPayloadForDebugRpc>.Fail("Transactions cannot be null or empty", ErrorCodes.InvalidRequest);
            }
            ExecutionPayloadForDebugRpc execPayloadForRpc = _mergeDebugBridge.GenerateNewPayloadWithTransactions(transactions);
            if (execPayloadForRpc == null)
            {
                return ResultWrapper<ExecutionPayloadForDebugRpc>.Fail("Failed to generate new payload with transactions", ErrorCodes.InternalError);
            }
            return ResultWrapper<ExecutionPayloadForDebugRpc>.Success(execPayloadForRpc);
        }
    }
}
