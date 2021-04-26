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

using System.Linq;
using System.Text;
using Nethermind.Blockchain.Tracing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Mev.Data;

namespace Nethermind.Mev.Execution
{
    public class CallTxBundleExecutor : TxBundleExecutor<TxsResults, BlockCallOutputTracer>
    {

        // protected override ResultWrapper<TxsToResults> ExecuteBundle(Transaction[] transactionCalls, BlockParameter? blockParameter, UInt256? blockTimestamp, CancellationToken token)
        // {
        //
        //     Stopwatch stopwatch = new Stopwatch();
        //     stopwatch.Start();
        //
        //     List<Block> suggestedBlocks = new List<Block> {new Block(headerNew, transactionCalls, new BlockHeader[0])};
        //     ParityLikeBlockTracer tracer = new(ParityTraceTypes.Trace);
        //     _blockProcessor.Process(stateRoot, suggestedBlocks, ProcessingOptions.Trace, tracer.WithCancellation(token));
        //     IReadOnlyCollection<ParityLikeTxTrace> results = tracer.BuildResult();
        //     // results.SelectMany(ParityTxTraceFromStore.FromTxTrace).ToArray();
        //     List<(Keccak, byte[])> pairs = new();
        //     foreach(var result in results)
        //     {
        //         pairs.Add((result.TransactionHash ?? Keccak.Zero, result.Output ?? new byte[0]));
        //     }
        //
        //     stopwatch.Stop();
        //     if (_logger.IsDebug) _logger.Debug($"Simulating eth_callBundle finished with runtime {stopwatch.Elapsed}");
        //
        //     return ResultWrapper<TxsToResults>.Success(new TxsToResults(pairs.ToArray()));
        // }
        
        public CallTxBundleExecutor(ITracer tracer) : base(tracer)
        {
        }

        protected override TxsResults BuildResult(MevBundle bundle, Block block, BlockCallOutputTracer blockTracer, Keccak resultStateRoot)
        {
            TxResult ToTxResult(CallOutputTracer callOutputTracer)
            {
                TxResult result = new TxResult();
                if (callOutputTracer.StatusCode == StatusCode.Success)
                {
                    result.Value = callOutputTracer.ReturnValue;
                }
                else
                {
                    // Is this right?
                    result.Error = Encoding.UTF8.GetBytes(callOutputTracer.Error);
                }
                return result;
            }

            return new TxsResults(blockTracer.BuildResults().ToDictionary(
                kvp => kvp.Key,
                kvp => ToTxResult(kvp.Value)));
        }
    }
}
