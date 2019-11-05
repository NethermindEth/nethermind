/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm.Tracing;
using Nethermind.Facade;
using Nethermind.JsonRpc.Data;
using Nethermind.Logging;
using Newtonsoft.Json;

namespace Nethermind.JsonRpc.Modules.Trace
{
    public class TraceModule : ITraceModule
    {
        private readonly IBlockchainBridge _blockchainBridge;
        private readonly ITracer _tracer;

        public TraceModule(IBlockchainBridge blockchainBridge, ILogManager logManager, ITracer tracer)
        {
            _blockchainBridge = blockchainBridge ?? throw new ArgumentNullException(nameof(blockchainBridge));
            _tracer = tracer ?? throw new ArgumentNullException(nameof(tracer));
        }

        private ParityTraceTypes GetParityTypes(string[] types)
        {
            return types.Select(s => (ParityTraceTypes) Enum.Parse(typeof(ParityTraceTypes), s, true)).Aggregate((t1, t2) => t1 | t2);
        }

        public ResultWrapper<ParityLikeTxTrace> trace_call(TransactionForRpc message, string[] traceTypes, BlockParameter quantity)
        {
            throw new NotImplementedException();
        }

        public ResultWrapper<ParityLikeTxTrace[]> trace_callMany((TransactionForRpc message, string[] traceTypes, BlockParameter numberOrTag)[] a)
        {
            throw new NotImplementedException();
        }

        public ResultWrapper<ParityLikeTxTrace> trace_rawTransaction(byte[] data, string[] traceTypes)
        {
            ParityLikeTxTrace result = _tracer.ParityTraceRawTransaction(data, GetParityTypes(traceTypes));
            return ResultWrapper<ParityLikeTxTrace>.Success(result);
        }

        public ResultWrapper<ParityLikeTxTrace> trace_replayTransaction(Keccak txHash, string[] traceTypes)
        {
            return ResultWrapper<ParityLikeTxTrace>.Success(_tracer.ParityTrace(txHash, GetParityTypes(traceTypes)));
        }

        public ResultWrapper<ParityLikeTxTrace[]> trace_replayBlockTransactions(BlockParameter blockParameter, string[] traceTypes)
        {
            Block block;
            try
            {
                block = _blockchainBridge.GetBlock(blockParameter, true, true);
                if (block is null)
                {
                    return ResultWrapper<ParityLikeTxTrace[]>.Success(null);
                }
            }
            catch (JsonRpcException ex)
            {
                return ResultWrapper<ParityLikeTxTrace[]>.Fail(ex.Message, ex.ErrorType, null);
            }

            ParityLikeTxTrace[] result = _tracer.ParityTraceBlock(block.Hash, GetParityTypes(traceTypes));
            return ResultWrapper<ParityLikeTxTrace[]>.Success(result);
        }

        public ResultWrapper<ParityLikeTxTrace[]> trace_filter(BlockParameter fromBlock, BlockParameter toBlock, Address toAddress, int after, int count)
        {
            throw new NotImplementedException();
        }

        public ResultWrapper<ParityLikeTxTrace[]> trace_block(BlockParameter blockParameter)
        {
            Block block;
            try
            {
                block = _blockchainBridge.GetBlock(blockParameter, true, true);
                if (block is null)
                {
                    return ResultWrapper<ParityLikeTxTrace[]>.Success(null);
                }
            }
            catch (JsonRpcException ex)
            {
                return ResultWrapper<ParityLikeTxTrace[]>.Fail(ex.Message, ex.ErrorType, null);
            }

            return ResultWrapper<ParityLikeTxTrace[]>.Success(_tracer.ParityTraceBlock(block.Hash,
                ParityTraceTypes.Trace));
        }

        public ResultWrapper<ParityLikeTxTrace> trace_get(Keccak txHash, int[] positions)
        {
            throw new NotImplementedException();
        }

        public ResultWrapper<ParityLikeTxTrace> trace_transaction(Keccak txHash)
        {
            return ResultWrapper<ParityLikeTxTrace>.Success(_tracer.ParityTrace(txHash, ParityTraceTypes.Trace));
        }
    }
}