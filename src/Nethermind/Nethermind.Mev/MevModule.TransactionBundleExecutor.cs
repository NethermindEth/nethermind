using System;
using System.Numerics;
using System.Threading;
using System.Linq;
using System.Diagnostics;
using System.Collections.Generic;
using Nethermind.JsonRpc.Modules;
using Nethermind.Core.Crypto;
using Nethermind.Blockchain.Find;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Data;
using Nethermind.Int256;
using Nethermind.Core;
using Nethermind.Facade;
using Nethermind.Trie;
using Nethermind.Logging;
using Nethermind.Blockchain.Processing;
using Nethermind.Evm.Tracing.ParityStyle;
using Nethermind.Evm.Tracing;
using Nethermind.JsonRpc.Modules.Trace;

namespace Nethermind.Mev
{
    public partial class MevRpcModule 
    {
        private static bool HasStateForBlock(IBlockchainBridge blockchainBridge, BlockHeader header)
        {
            RootCheckVisitor rootCheckVisitor = new();
            if (header.StateRoot == null) return false;
            blockchainBridge.RunTreeVisitor(rootCheckVisitor, header.StateRoot!);
            return rootCheckVisitor.HasRoot;
        }
        
        private abstract class TxBundleExecutor<TResult>
        {
            protected readonly IBlockchainBridge _blockchainBridge;
            protected readonly IBlockFinder _blockTree;
            protected readonly IJsonRpcConfig _rpcConfig;
            protected readonly ILogger _logger;
            protected readonly IBlockProcessor _blockProcessor;


            protected TxBundleExecutor(IBlockchainBridge blockchainBridge, IBlockFinder blockFinder, IJsonRpcConfig rpcConfig, ILogger logger, IBlockProcessor blockProcessor)
            {
                _blockchainBridge = blockchainBridge;
                _blockTree = blockFinder;
                _rpcConfig = rpcConfig;
                _logger = logger;
                _blockProcessor = blockProcessor;
            }
            
            public ResultWrapper<TResult> ExecuteBundleTx(
                TransactionForRpc[] transactionCalls, 
                BlockParameter blockParameter,
                UInt256? blockTimestamp)
            { 
                foreach(var txForRpc in transactionCalls)
                {
                    FixCallTx(txForRpc);
                }
                using CancellationTokenSource cancellationTokenSource = new(_rpcConfig.Timeout);
                Transaction[] transactions = transactionCalls.Select(txForRpc => txForRpc.ToTransaction(_blockchainBridge.GetChainId())).ToArray();
                return ExecuteBundleTx(transactions, blockParameter, blockTimestamp, cancellationTokenSource.Token);
            }

            protected abstract ResultWrapper<TResult> ExecuteBundleTx(Transaction[] transactionCalls, BlockParameter blockParameter, UInt256? blockTimestamp, CancellationToken token);

            protected ResultWrapper<TResult> GetInputError(BlockchainBridge.CallOutput result) => 
                ResultWrapper<TResult>.Fail(result.Error ?? "", ErrorCodes.InvalidInput);
            
            private void FixCallTx(TransactionForRpc transactionCall)
            {
                if (transactionCall.Gas == null || transactionCall.Gas == 0)
                {
                    transactionCall.Gas = _rpcConfig.GasCap ?? long.MaxValue;
                }
                else
                {
                    transactionCall.Gas = Math.Min(_rpcConfig.GasCap ?? long.MaxValue, transactionCall.Gas.Value);
                }

                transactionCall.From ??= Address.SystemUser;
            }
        }
        
        private class CallBundleTxExecutor : TxBundleExecutor<TxsToResults>
        {
            public CallBundleTxExecutor(IBlockchainBridge blockchainBridge, IBlockFinder blockFinder, IJsonRpcConfig rpcConfig, ILogger logger, IBlockProcessor blockProcessor)
                : base(blockchainBridge, blockFinder, rpcConfig, logger, blockProcessor)
            {
            }

            protected override ResultWrapper<TxsToResults> ExecuteBundleTx(Transaction[] transactionCalls, BlockParameter blockParameter, UInt256? blockTimestamp, CancellationToken token)
            {
                if (transactionCalls.Length == 0) 
                    return ResultWrapper<TxsToResults>.Fail("no tx specified in bundle");

                SearchResult<BlockHeader> searchResult = _blockTree.SearchForHeader(blockParameter);
                if (searchResult.IsError) 
                {
                    return ResultWrapper<TxsToResults>.Fail(searchResult);
                } 
                BlockHeader header = searchResult.Object!;
                if (!HasStateForBlock(_blockchainBridge, header!))
                {
                    return ResultWrapper<TxsToResults>.Fail($"No state available for block {header.Hash}", ErrorCodes.ResourceUnavailable);
                }

                Keccak? parentHash = header.ParentHash;
                if (parentHash == null)
                {
                    return ResultWrapper<TxsToResults>.Fail($"No parent hash for block {header.Hash}", ErrorCodes.ResourceUnavailable);
                }
                SearchResult<BlockHeader> searchResultParent = _blockTree.SearchForHeader(new BlockParameter(parentHash!));
                if (searchResultParent.IsError) 
                {
                    return ResultWrapper<TxsToResults>.Fail(searchResultParent);
                } 
                BlockHeader headerParent = searchResultParent.Object!;
                if (!HasStateForBlock(_blockchainBridge, headerParent))
                {
                    return ResultWrapper<TxsToResults>.Fail($"No state available for block {headerParent.Hash}", ErrorCodes.ResourceUnavailable);
                }
                Keccak stateRoot = headerParent.StateRoot!;

                long blockNumber = headerParent.Number + 1;
                UInt256 timestamp = blockTimestamp ?? headerParent.Timestamp;
                long gasLimit = headerParent.GasLimit;
                Keccak ommersHash = Keccak.OfAnEmptySequenceRlp;
                Address beneficiary = Address.Zero;
                UInt256 difficulty = headerParent.Difficulty;

                BlockHeader headerNew = new BlockHeader(parentHash!, ommersHash, beneficiary, difficulty, blockNumber, gasLimit, timestamp, new byte[0]);  

                // ITimer timer = _mevPlugin.NethermindApi.TimerFactory.CreateTimer(TimeSpan.FromSeconds(5));
                // timer.Elapsed += (object sender, EventArgs e) => {
                //     // TODO
                // };
                // timer.Start(); 
                // BlockchainBridge.CallOutput result = _blockchainBridge.CallBundle(headerNew, transactionCalls, token);
            
                // if (result.Error is null)
                // {
                //     return ResultWrapper<string>.Success(result.OutputData.ToHexString(true));
                // }

                // return result.InputError
                //     ? GetInputError(result)
                //     : ResultWrapper<TxsToResults>.Fail("VM execution error.", ErrorCodes.ExecutionError, result.Error);
                
                // timer.Stop();
                // if (_logger.IsDebug) _logger.Debug($"Executing EVM call finished with runtime {timer.IntervalMilliseconds} ms");

                Stopwatch stopwatch = new Stopwatch();
                stopwatch.Start();

                List<Block> suggestedBlocks = new List<Block> {new Block(headerNew, transactionCalls, new BlockHeader[0])};
                ParityLikeBlockTracer tracer = new(ParityTraceTypes.Trace);
                _blockProcessor.Process(stateRoot, suggestedBlocks, ProcessingOptions.Trace, tracer.WithCancellation(token));
                IReadOnlyCollection<ParityLikeTxTrace> results = tracer.BuildResult();
                // results.SelectMany(ParityTxTraceFromStore.FromTxTrace).ToArray();
                List<(Keccak, byte[])> pairs = new();
                foreach(var result in results)
                {
                    pairs.Add((result.TransactionHash ?? Keccak.Zero, result.Output ?? new byte[0]));
                }

                stopwatch.Stop();
                if (_logger.IsDebug) _logger.Debug($"Simulating eth_callBundle finished with runtime {stopwatch.Elapsed}");

                return ResultWrapper<TxsToResults>.Success(new TxsToResults(pairs));
            }
        }
    
    }
}