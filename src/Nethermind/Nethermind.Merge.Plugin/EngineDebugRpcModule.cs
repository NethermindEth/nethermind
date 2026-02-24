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
    // same name as DebugRpcModule in Nethermind.JsonRpc.Modules.DebugModule so they both get activated by same name (Debug)
    public class DebugRpcModule : IDebugRpcModule
    {
        private readonly IEngineDebugBridge _mergeDebugBridge;

        public DebugRpcModule(IEngineDebugBridge debugBridge)
        {
            _mergeDebugBridge = debugBridge ?? throw new ArgumentNullException(nameof(debugBridge));
        }

        public ResultWrapper<Hash256> debug_calculateBlockHash(ExecutionPayload executionPayload)
        {
            try
            {
                Hash256 blockHash = _mergeDebugBridge.CalculateBlockHash(executionPayload);
                return ResultWrapper<Hash256>.Success(blockHash);
            }
            catch
            {
                return ResultWrapper<Hash256>.Fail("Failed to calculate block hash", ErrorCodes.InternalError);
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
