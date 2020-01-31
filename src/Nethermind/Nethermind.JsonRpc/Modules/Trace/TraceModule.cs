//  Copyright (c) 2018 Demerzel Solutions Limited
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
using System.Linq;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Tracing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.Tracing.ParityStyle;
using Nethermind.Facade;
using Nethermind.JsonRpc.Data;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;

namespace Nethermind.JsonRpc.Modules.Trace
{
    public class TraceModule : ITraceModule
    {
        private readonly IReceiptStorage _receiptStorage;
        private readonly ITracer _tracer;
        private readonly IBlockFinder _blockFinder;
        private readonly TransactionDecoder _txDecoder = new TransactionDecoder();

        public TraceModule(IReceiptStorage receiptStorage, ITracer tracer, IBlockFinder blockFinder)
        {
            _receiptStorage = receiptStorage ?? throw new ArgumentNullException(nameof(receiptStorage));
            _tracer = tracer ?? throw new ArgumentNullException(nameof(tracer));
            _blockFinder = blockFinder ?? throw new ArgumentNullException(nameof(blockFinder));
        }

        private static ParityTraceTypes GetParityTypes(string[] types)
        {
            return types.Select(s => (ParityTraceTypes) Enum.Parse(typeof(ParityTraceTypes), s, true)).Aggregate((t1, t2) => t1 | t2);
        }

        public ResultWrapper<ParityTxTraceFromReplay> trace_call(TransactionForRpc message, string[] traceTypes, BlockParameter quantity)
        {
            throw new NotImplementedException();
        }

        public ResultWrapper<ParityTxTraceFromReplay[]> trace_callMany((TransactionForRpc message, string[] traceTypes, BlockParameter numberOrTag)[] a)
        {
            throw new NotImplementedException();
        }

        public ResultWrapper<ParityTxTraceFromReplay> trace_rawTransaction(byte[] data, string[] traceTypes)
        {
            SearchResult<BlockHeader> headerSearch = _blockFinder.SearchForHeader(BlockParameter.Latest);
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

            Transaction tx = _txDecoder.Decode(new RlpStream(data));
            Block block = new Block(header, new[] {tx}, Enumerable.Empty<BlockHeader>());

            ParityLikeTxTrace[] result = TraceBlock(block, GetParityTypes(traceTypes));
            return ResultWrapper<ParityTxTraceFromReplay>.Success(new ParityTxTraceFromReplay(result.SingleOrDefault()));
        }

        public ResultWrapper<ParityTxTraceFromReplay> trace_replayTransaction(Keccak txHash, string[] traceTypes)
        {
            SearchResult<TxReceipt> receiptSearch = _receiptStorage.SearchForReceipt(txHash);
            if (receiptSearch.IsError)
            {
                return ResultWrapper<ParityTxTraceFromReplay>.Fail(receiptSearch);
            }

            TxReceipt receipt = receiptSearch.Object;
            SearchResult<Block> blockSearch = _blockFinder.SearchForBlock(new BlockParameter(receipt.BlockHash));
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

            ParityLikeTxTrace[] txTraces = TraceBlock(block, GetParityTypes(traceTypes));

            // ReSharper disable once CoVariantArrayConversion
            return ResultWrapper<ParityTxTraceFromReplay[]>.Success(txTraces.Select(t => new ParityTxTraceFromReplay(t)).ToArray());
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

            ParityLikeTxTrace[] txTraces = TraceBlock(block, ParityTraceTypes.Trace | ParityTraceTypes.Rewards);
            return ResultWrapper<ParityTxTraceFromStore[]>.Success(txTraces.SelectMany(ParityTxTraceFromStore.FromTxTrace).ToArray());
        }

        public ResultWrapper<ParityTxTraceFromStore[]> trace_get(Keccak txHash, int[] positions)
        {
            throw new NotImplementedException();
        }

        public ResultWrapper<ParityTxTraceFromStore[]> trace_transaction(Keccak txHash)
        {
            SearchResult<TxReceipt> receiptSearch = _receiptStorage.SearchForReceipt(txHash);
            if (receiptSearch.IsError)
            {
                return ResultWrapper<ParityTxTraceFromStore[]>.Fail(receiptSearch);
            }

            TxReceipt receipt = receiptSearch.Object;
            SearchResult<Block> blockSearch = _blockFinder.SearchForBlock(new BlockParameter(receipt.BlockHash));
            if (blockSearch.IsError)
            {
                return ResultWrapper<ParityTxTraceFromStore[]>.Fail(blockSearch);
            }

            Block block = blockSearch.Object;

            ParityLikeTxTrace txTrace = TraceTx(block, txHash, ParityTraceTypes.Trace | ParityTraceTypes.Rewards);
            return ResultWrapper<ParityTxTraceFromStore[]>.Success(ParityTxTraceFromStore.FromTxTrace(txTrace));
        }

        private ParityLikeTxTrace[] TraceBlock(Block block, ParityTraceTypes traceTypes)
        {
            ParityLikeBlockTracer listener = new ParityLikeBlockTracer(traceTypes);
            _tracer.Trace(block, listener);
            return listener.BuildResult().ToArray();
        }

        private ParityLikeTxTrace TraceTx(Block block, Keccak txHash, ParityTraceTypes traceTypes)
        {
            ParityLikeBlockTracer listener = new ParityLikeBlockTracer(txHash, traceTypes);
            _tracer.Trace(block, listener);
            return listener.BuildResult().SingleOrDefault();
        }
    }
}