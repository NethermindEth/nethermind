// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
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
    public class MergeDebugRpc : DebugRpcModule, IMergeDebugModule
    {
        private readonly IMergeDebugBridge _mergeDebugBridge;
        public MergeDebugRpc(ILogManager logManager, IMergeDebugBridge debugBridge, IJsonRpcConfig jsonRpcConfig, ISpecProvider specProvider) :
            base(logManager, (DebugBridge)debugBridge, jsonRpcConfig, specProvider)
        {
            _mergeDebugBridge = debugBridge ?? throw new ArgumentNullException(nameof(debugBridge));
        }

        public ResultWrapper<Hash256> debug_calculateBlockHash(ExecutionPayload executionPayload)
        {
            if (executionPayload == null)
            {
                return ResultWrapper<Hash256>.Fail("Execution payload cannot be null", ErrorCodes.InvalidRequest);
            }
            // Assuming we have a method to calculate the block hash from the execution payload
            if(executionPayload.TryGetBlock(out Block? block))
            {
                Hash256 blockHash = block.Header.GetOrCalculateHash();
                return ResultWrapper<Hash256>.Success(blockHash);
            }
            else
            {
                return ResultWrapper<Hash256>.Fail("Invalid execution payload", ErrorCodes.InvalidRequest);
            }
        }

        public ResultWrapper<ExecutionPayloadForRpc> debug_generateNewPayload(BlockParameter blockParameter)
        {
            if (blockParameter == null)
            {
                return ResultWrapper<ExecutionPayloadForRpc>.Fail("Block parameter cannot be null", ErrorCodes.InvalidRequest);
            }

            ExecutionPayloadForRpc execPayloadForRpc = _mergeDebugBridge.GenerateNewPayload(blockParameter);

            if (execPayloadForRpc == null)
            {
                return ResultWrapper<ExecutionPayloadForRpc>.Fail("Failed to generate new payload", ErrorCodes.InternalError);
            }
            return ResultWrapper<ExecutionPayloadForRpc>.Success(execPayloadForRpc);
        }

        public ResultWrapper<ExecutionPayloadForRpc> debug_generateNewPayload(BlockParameter blockParameterStart, BlockParameter blockParameterEnd)
        {
            throw new NotImplementedException();
        }

        public ResultWrapper<ExecutionPayloadForRpc> debug_generateNewPayloadWithTransactions(TransactionForRpc[] transactions)
        {
            if (transactions == null || transactions.Length == 0)
            {
                return ResultWrapper<ExecutionPayloadForRpc>.Fail("Transactions cannot be null or empty", ErrorCodes.InvalidRequest);
            }
            ExecutionPayloadForRpc execPayloadForRpc = _mergeDebugBridge.GenerateNewPayloadWithTransactions(transactions);
            if (execPayloadForRpc == null)
            {
                return ResultWrapper<ExecutionPayloadForRpc>.Fail("Failed to generate new payload with transactions", ErrorCodes.InternalError);
            }
            return ResultWrapper<ExecutionPayloadForRpc>.Success(execPayloadForRpc);
        }
    }
}
