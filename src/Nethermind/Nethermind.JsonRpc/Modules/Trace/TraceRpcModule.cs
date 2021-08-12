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

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Tracing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.Tracing.ParityStyle;
using Nethermind.JsonRpc.Data;
using Nethermind.Serialization.Rlp;

namespace Nethermind.JsonRpc.Modules.Trace
{
    public class TraceRpcModule : ITraceRpcModule
    {
        private readonly IReceiptFinder _receiptFinder;
        private readonly ITracer _tracer;
        private readonly IBlockFinder _blockFinder;
        private readonly TxDecoder _txDecoder = new();
        private readonly IJsonRpcConfig _jsonRpcConfig;
        private readonly TimeSpan _cancellationTokenTimeout;

        public TraceRpcModule(IReceiptFinder receiptFinder, ITracer tracer, IBlockFinder blockFinder, IJsonRpcConfig jsonRpcConfig)
        {
            _receiptFinder = receiptFinder ?? throw new ArgumentNullException(nameof(receiptFinder));
            _tracer = tracer ?? throw new ArgumentNullException(nameof(tracer));
            _blockFinder = blockFinder ?? throw new ArgumentNullException(nameof(blockFinder));
            _jsonRpcConfig = jsonRpcConfig ?? throw new ArgumentNullException(nameof(jsonRpcConfig));
            _cancellationTokenTimeout = TimeSpan.FromMilliseconds(_jsonRpcConfig.Timeout);
        }

        private static ParityTraceTypes GetParityTypes(string[] types)
        {
            return types.Select(s => (ParityTraceTypes) Enum.Parse(typeof(ParityTraceTypes), s, true)).Aggregate((t1, t2) => t1 | t2);
        }

        public ResultWrapper<ParityTxTraceFromReplay> trace_call(TransactionForRpc message, string[] traceTypes, BlockParameter blockParameter)
        {
            Transaction tx = message.ToTransaction();
            return TraceTx(tx, traceTypes, blockParameter);
        }

        public ResultWrapper<ParityTxTraceFromReplay[]> trace_callMany((TransactionForRpc message, string[] traceTypes, BlockParameter numberOrTag)[] a)
        {
            throw new NotImplementedException();
        }

        public ResultWrapper<ParityTxTraceFromReplay> trace_rawTransaction(byte[] data, string[] traceTypes)
        {
            Transaction tx = _txDecoder.Decode(new RlpStream(data), RlpBehaviors.SkipTypedWrapping);
            return TraceTx(tx, traceTypes, BlockParameter.Latest);
        }

        private ResultWrapper<ParityTxTraceFromReplay> TraceTx(Transaction tx, string[] traceTypes, BlockParameter blockParameter)
        {
            SearchResult<BlockHeader> headerSearch = _blockFinder.SearchForHeader(blockParameter);
            if (headerSearch.IsError)
            {
                return ResultWrapper<ParityTxTraceFromReplay>.Fail(headerSearch);
            }

            BlockHeader header = headerSearch.Object;

            if (header.IsGenesis)
            {
                header = new BlockHeader(
                    header.Hash,
                    Keccak.OfAnEmptySequenceRlp,
                    Address.Zero,
                    header.Difficulty,
                    header.Number + 1,
                    header.GasLimit,
                    header.Timestamp + 1,
                    header.ExtraData);

                header.TotalDifficulty = 2 * header.Difficulty;
            }

            Block block = new(header, new[] {tx}, Enumerable.Empty<BlockHeader>());

            IReadOnlyCollection<ParityLikeTxTrace> result = TraceBlock(block, GetParityTypes(traceTypes));
            return ResultWrapper<ParityTxTraceFromReplay>.Success(new ParityTxTraceFromReplay(result.SingleOrDefault()));
        }

        public ResultWrapper<ParityTxTraceFromReplay> trace_replayTransaction(Keccak txHash, string[] traceTypes)
        {
            SearchResult<Keccak> blockHashSearch = _receiptFinder.SearchForReceiptBlockHash(txHash);
            if (blockHashSearch.IsError)
            {
                return ResultWrapper<ParityTxTraceFromReplay>.Fail(blockHashSearch);
            }

            SearchResult<Block> blockSearch = _blockFinder.SearchForBlock(new BlockParameter(blockHashSearch.Object));
            if (blockSearch.IsError)
            {
                return ResultWrapper<ParityTxTraceFromReplay>.Fail(blockSearch);
            }

            Block block = blockSearch.Object;

            ParityLikeTxTrace txTrace = TraceTx(block, txHash, GetParityTypes(traceTypes));
            return ResultWrapper<ParityTxTraceFromReplay>.Success(new ParityTxTraceFromReplay(txTrace));
        }

        public ResultWrapper<ParityTxTraceFromReplay[]> trace_replayBlockTransactions(BlockParameter blockParameter, string[] traceTypes)
        {
            SearchResult<Block> blockSearch = _blockFinder.SearchForBlock(blockParameter);
            if (blockSearch.IsError)
            {
                return ResultWrapper<ParityTxTraceFromReplay[]>.Fail(blockSearch);
            }

            Block block = blockSearch.Object;

            IReadOnlyCollection<ParityLikeTxTrace> txTraces = TraceBlock(block, GetParityTypes(traceTypes));

            // ReSharper disable once CoVariantArrayConversion
            return ResultWrapper<ParityTxTraceFromReplay[]>.Success(txTraces.Select(t => new ParityTxTraceFromReplay(t, true)).ToArray());
        }

        public ResultWrapper<ParityTxTraceFromStore[]> trace_filter(BlockParameter fromBlock, BlockParameter toBlock, Address toAddress, int after, int count)
        {
            throw new NotImplementedException();
        }

        public ResultWrapper<ParityTxTraceFromStore[]> trace_block(BlockParameter blockParameter)
        {
            SearchResult<Block> blockSearch = _blockFinder.SearchForBlock(blockParameter);
            if (blockSearch.IsError)
            {
                return ResultWrapper<ParityTxTraceFromStore[]>.Fail(blockSearch);
            }

            Block block = blockSearch.Object;
            
            IReadOnlyCollection<ParityLikeTxTrace> txTraces = TraceBlock(block, ParityTraceTypes.Trace | ParityTraceTypes.Rewards);
            return ResultWrapper<ParityTxTraceFromStore[]>.Success(txTraces.SelectMany(ParityTxTraceFromStore.FromTxTrace).ToArray());
        }

        public ResultWrapper<ParityTxTraceFromStore[]> trace_get(Keccak txHash, int[] positions)
        {
            throw new NotImplementedException();
        }

        public ResultWrapper<ParityTxTraceFromStore[]> trace_transaction(Keccak txHash)
        {
            SearchResult<Keccak> blockHashSearch = _receiptFinder.SearchForReceiptBlockHash(txHash);
            if (blockHashSearch.IsError)
            {
                return ResultWrapper<ParityTxTraceFromStore[]>.Fail(blockHashSearch);
            }
            
            SearchResult<Block> blockSearch = _blockFinder.SearchForBlock(new BlockParameter(blockHashSearch.Object));
            if (blockSearch.IsError)
            {
                return ResultWrapper<ParityTxTraceFromStore[]>.Fail(blockSearch);
            }

            Block block = blockSearch.Object;

            ParityLikeTxTrace txTrace = TraceTx(block, txHash, ParityTraceTypes.Trace | ParityTraceTypes.Rewards);
            return ResultWrapper<ParityTxTraceFromStore[]>.Success(ParityTxTraceFromStore.FromTxTrace(txTrace));
        }

        private IReadOnlyCollection<ParityLikeTxTrace> TraceBlock(Block block, ParityTraceTypes traceTypes)
        {
            using CancellationTokenSource cancellationTokenSource = new(_cancellationTokenTimeout);
            CancellationToken cancellationToken = cancellationTokenSource.Token;

            ParityLikeBlockTracer listener = new(traceTypes);
            _tracer.Trace(block, listener.WithCancellation(cancellationToken));

            return listener.BuildResult();
        }

        private ParityLikeTxTrace TraceTx(Block block, Keccak txHash, ParityTraceTypes traceTypes)
        {
            using CancellationTokenSource cancellationTokenSource = new(_cancellationTokenTimeout);
            CancellationToken cancellationToken = cancellationTokenSource.Token;

            ParityLikeBlockTracer listener = new(txHash, traceTypes);
            _tracer.Trace(block, listener.WithCancellation(cancellationToken));

            return listener.BuildResult().SingleOrDefault();
        }
    }
}
