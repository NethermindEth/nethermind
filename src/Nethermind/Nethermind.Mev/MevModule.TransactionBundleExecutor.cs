// using System;
// using System.Numerics;
// using System.Threading;
// using System.Linq;
// using System.Collections.Generic;
// using Nethermind.Blockchain.Find;
// using Nethermind.JsonRpc;
// using Nethermind.JsonRpc.Data;
// using Nethermind.Int256;
// using Nethermind.Core;
// using Nethermind.Facade;
// using Nethermind.Trie;

// namespace Nethermind.Mev
// {
//     public partial class MevRpcModule 
//     {
//         private static bool HasStateForBlock(IBlockchainBridge blockchainBridge, BlockHeader header)
//         {
//             RootCheckVisitor rootCheckVisitor = new();
//             if (header.StateRoot == null) return false;
//             blockchainBridge.RunTreeVisitor(rootCheckVisitor, header.StateRoot!);
//             return rootCheckVisitor.HasRoot;
//         }
//         // Duplicated code, but no dependency, fully modular
//         private abstract class TxBundleExecutor<TResult>
//         {
//             protected readonly IBlockchainBridge _blockchainBridge;
//             private readonly IBlockFinder _blockTree;
//             private readonly IJsonRpcConfig _rpcConfig;

//             protected TxBundleExecutor(IBlockchainBridge blockchainBridge, IBlockFinder blockFinder, IJsonRpcConfig rpcConfig)
//             {
//                 _blockchainBridge = blockchainBridge;
//                 _blockTree = blockFinder;
//                 _rpcConfig = rpcConfig;
//             }
            
//             public ResultWrapper<TResult> ExecuteBundleTx(
//                 TransactionForRpc[] transactionCalls, 
//                 BlockParameter blockParameter,
//                 UInt256? blockTimestamp)
//             {
//                 SearchResult<BlockHeader> searchResult = _blockTree.SearchForHeader(blockParameter);
//                 if (searchResult.IsError) 
//                 {
//                     return ResultWrapper<TxToResult>.Fail(searchResult);
//                 } 
//                 BlockHeader header = searchResult.Object;
//                 if (!HasStateForBlock(_blockchainBridge, header))
//                 {
//                     return ResultWrapper<TxToResult>.Fail($"No state available for block {header.Hash}", ErrorCodes.ResourceUnavailable);
//                 }

//                 Keccak? parentHash = header.ParentHash;
//                 if (parentHash == null)
//                 {
//                     return ResultWrapper<TxToResult>.Fail($"No parent hash for block {header.hash}", ErrorCodes.ResourceUnavailable);
//                 }
//                 SearchResult<BlockHeader> searchResultParent = _blockTree.SearchForHeader(new BlockParameter(parentHash));
//                 if (searchResultParent.IsError) 
//                 {
//                     return ResultWrapper<TxToResult>.Fail(searchResultParent);
//                 } 
//                 BlockHeader headerParent = searchResultParent.Object;
//                 if (!HasStateForBlock(_blockchainBridge, headerParent))
//                 {
//                     return ResultWrapper<TxToResult>.Fail($"No state available for block {headerParent.Hash}", ErrorCodes.ResourceUnavailable);
//                 }

//                 long blockNumber = headerParent.Number + 1;
//                 UInt256 timestamp = headerParent.Timestamp;
//                 if (blockTimestamp != null) timestamp = blockTimestamp!;
                
//                 // TODO create header

//                 // TODO fix all 
//                 FixCallTx(transactionCall);

//                 using CancellationTokenSource cancellationTokenSource = new(_rpcConfig.Timeout);
//                 Transaction[] txs = transactionCalls.Select(txForRpc => txForRpc.ToTransaction(_blockchainBridge.GetChainId())).ToArray();
//                 // wrong header
//                 return ExecuteBundleTx(header, txs, cancellationTokenSource.Token);
//             }

//             protected abstract ResultWrapper<TResult> ExecuteBundleTx(BlockHeader header, Transaction[] txs, CancellationToken token);

//             protected ResultWrapper<TResult> GetInputError(BlockchainBridge.CallOutput result) => 
//                 ResultWrapper<TResult>.Fail(result.Error, ErrorCodes.InvalidInput);
            
//             private void FixCallTx(TransactionForRpc transactionCall)
//             {
//                 if (transactionCall.Gas == null || transactionCall.Gas == 0)
//                 {
//                     transactionCall.Gas = _rpcConfig.GasCap ?? long.MaxValue;
//                 }
//                 else
//                 {
//                     transactionCall.Gas = Math.Min(_rpcConfig.GasCap ?? long.MaxValue, transactionCall.Gas.Value);
//                 }

//                 transactionCall.From ??= Address.SystemUser;
//             }
//         }
        
//         private class CallBundleTxExecutor : TxBundleExecutor<TxToResult>
//         {
//             public CallBundleTxExecutor(IBlockchainBridge blockchainBridge, IBlockFinder blockFinder, IJsonRpcConfig rpcConfig)
//                 : base(blockchainBridge, blockFinder, rpcConfig)
//             {
//             }

//             protected override ResultWrapper<TxToResult> ExecuteBundleTx(BlockHeader header, Transaction[] txs, CancellationToken token)
//             {
//                 if (txs.Length == 0) return ResultWrapper<TxToResult>.Fail("no tx specified in bundle");

//                 // TODO use cancellation token
//                 // ITimer timer = _mevPlugin.NethermindApi.TimerFactory.CreateTimer(TimeSpan.FromSeconds(5));
//                 // timer.Elapsed += (object sender, EventArgs e) => {
//                 //     // TODO
//                 // };
//                 // timer.Start(); 
                
//                 BlockchainBridge.CallOutput result = _blockchainBridge.CallBundle(header, txs, token);
            
//                 if (result.Error is null)
//                 {
//                     // return ResultWrapper<string>.Success(result.OutputData.ToHexString(true));
//                 }

//                 // return result.InputError
//                 //     ? GetInputError(result)
//                 //     : ResultWrapper<string>.Fail("VM execution error.", ErrorCodes.ExecutionError, result.Error);

//                 throw new NotImplementedException();
                
//                 // timer.Stop();
//                 // if (_logger.IsDebug) _logger.Debug($"Executing EVM call finished with runtime {timer.IntervalMilliseconds} ms");
//             }
//         }
    
//     }
// }