// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.IdentityModel.Tokens;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Db;
using Nethermind.Evm;
using Nethermind.Facade;
using Nethermind.Facade.Proxy.Models.MultiCall;
using Nethermind.Int256;
using Nethermind.JsonRpc.Data;
using Nethermind.JsonRpc.Modules.Eth.Multicall;
using Nethermind.Specs.Forks;

namespace Nethermind.JsonRpc.Modules.Eth;

public partial class EthRpcModule
{
    private abstract class TxExecutor<TResult>
    {
        protected readonly IBlockchainBridge _blockchainBridge;
        private readonly IBlockFinder _blockFinder;
        private readonly IJsonRpcConfig _rpcConfig;

        protected TxExecutor(IBlockchainBridge blockchainBridge, IBlockFinder blockFinder, IJsonRpcConfig rpcConfig)
        {
            _blockchainBridge = blockchainBridge;
            _blockFinder = blockFinder;
            _rpcConfig = rpcConfig;
        }

        public ResultWrapper<TResult> ExecuteTx(
            TransactionForRpc transactionCall,
            BlockParameter? blockParameter)
        {
            SearchResult<BlockHeader> searchResult = _blockFinder.SearchForHeader(blockParameter);
            if (searchResult.IsError) return ResultWrapper<TResult>.Fail(searchResult);

            BlockHeader header = searchResult.Object;
            if (!HasStateForBlock(_blockchainBridge, header))
            {
                return ResultWrapper<TResult>.Fail($"No state available for block {header.Hash}",
                    ErrorCodes.ResourceUnavailable);
            }

            transactionCall.EnsureDefaults(_rpcConfig.GasCap);

            using CancellationTokenSource cancellationTokenSource = new(_rpcConfig.Timeout);
            Transaction tx = transactionCall.ToTransaction(_blockchainBridge.GetChainId());
            return ExecuteTx(header.Clone(), tx, cancellationTokenSource.Token);
        }

        protected abstract ResultWrapper<TResult>
            ExecuteTx(BlockHeader header, Transaction tx, CancellationToken token);

        protected ResultWrapper<TResult> GetInputError(BlockchainBridge.CallOutput result)
        {
            return ResultWrapper<TResult>.Fail(result.Error, ErrorCodes.InvalidInput);
        }
    }

    private class CallTxExecutor : TxExecutor<string>
    {
        public CallTxExecutor(IBlockchainBridge blockchainBridge, IBlockFinder blockFinder, IJsonRpcConfig rpcConfig)
            : base(blockchainBridge, blockFinder, rpcConfig)
        {
        }

        protected override ResultWrapper<string> ExecuteTx(BlockHeader header, Transaction tx, CancellationToken token)
        {
            BlockchainBridge.CallOutput result = _blockchainBridge.Call(header, tx, token);

            if (result.Error is null) return ResultWrapper<string>.Success(result.OutputData.ToHexString(true));

            return result.InputError
                ? GetInputError(result)
                : ResultWrapper<string>.Fail("VM execution error.", ErrorCodes.ExecutionError, result.Error);
        }
    }

    public class MultiCallTxExecutor
    {
        private readonly IDbProvider _dbProvider;
        private readonly IJsonRpcConfig _rpcConfig;
        private readonly ISpecProvider _specProvider;

        public MultiCallTxExecutor(IDbProvider DbProvider, ISpecProvider specProvider, IJsonRpcConfig rpcConfig)
        {
            _dbProvider = DbProvider;
            _specProvider = specProvider;
            _rpcConfig = rpcConfig;
        }

        private UInt256 MaxGas => GetMaxGas(_rpcConfig);


        public static UInt256 GetMaxGas(IJsonRpcConfig config)
        {
            return (UInt256)config.GasCap * (UInt256)config.GasCapMultiplier;
        }

        //ToDO move to exctensions
        private static IEnumerable<UInt256> Range(UInt256 start, UInt256 end)
        {
            for (UInt256 i = start; i <= end; i++) yield return i;
        }

        public ResultWrapper<MultiCallBlockResult[]> Execute(ulong version,
            MultiCallBlockStateCallsModel[] blockCallsToProcess,
            BlockParameter? blockParameter, bool traceTransfers)
        {
            IEnumerable<UInt256>? selsectResults = blockCallsToProcess.Select(model => model.BlockOverride.Number);
            //Was not able to overcome all of the protections related to setting of new block as BlockTree head so will keep it fair for now

            List<MultiCallBlockResult> results = new();
            using (MultiCallBlockchainFork tmpChain = new(_dbProvider, _specProvider, MaxGas))
            {
                List<MultiCallBlockStateCallsModel> blockCalls =
                    FillInMisingBlocks(blockCallsToProcess,
                        (ulong)tmpChain.LatestBlock
                            .Number); // blockCallsToProcess.OrderBy(model => model.BlockOverride.Number).ToList();

                tmpChain.BlockTracer.Trace = traceTransfers;
                ulong logIndices = 0;
                foreach (MultiCallBlockStateCallsModel blockCall in blockCalls)
                {
                    (bool processed, Block? block) = tmpChain.ForgeChainBlock(blockCall);

                    if (!processed) break;

                    if (selsectResults.Contains(blockCall.BlockOverride.Number))
                    {
                        List<MultiCallCallResult>? txResults = new List<MultiCallCallResult>();
                        foreach (Transaction? tx in tmpChain.LatestBlock.Transactions)
                        {
                            TxReceipt Receipt = null;
                            Receipt = tmpChain.receiptStorage.Get(tmpChain.LatestBlock.Hash)
                                .ForTransaction(tx.Hash);


                            IEnumerable<LogEntry> txActions = null;

                            if (traceTransfers)
                                txActions = tmpChain.BlockTracer.TxActions[tx.Hash];
                            else
                                txActions = Receipt.Logs;

                            Log[] logs = txActions.Select(entry => new Log
                            {
                                Data = entry.Data,
                                Address = entry.LoggersAddress,
                                Topics = entry.Topics,
                                BlockHash = tmpChain.LatestBlock.Hash,
                                BlockNumber = (ulong)tmpChain.LatestBlock.Number,
                                LogIndex = ++logIndices
                            }).ToArray();

                            txResults.Add(new MultiCallCallResult
                            {
                                GasUsed = (ulong)Receipt.GasUsed,
                                Error = new Facade.Proxy.Models.MultiCall.Error
                                {
                                    Data = (Receipt.Error.IsNullOrEmpty() ? Receipt.Error : "") ??
                                               string.Empty
                                },
                                Return = Receipt.ReturnValue,
                                Status = Receipt.StatusCode.ToString(),
                                Logs = logs.ToArray()
                            }
                            );
                        }

                        MultiCallBlockResult? result = new MultiCallBlockResult
                        {
                            baseFeePerGas = tmpChain.LatestBlock.BaseFeePerGas,
                            FeeRecipient = tmpChain.LatestBlock.Beneficiary,
                            GasLimit = (ulong)tmpChain.LatestBlock.GasLimit,
                            GasUsed = (ulong)tmpChain.LatestBlock.GasUsed,
                            Number = (ulong)tmpChain.LatestBlock.Number,
                            Hash = tmpChain.LatestBlock.Hash,
                            Timestamp = tmpChain.LatestBlock.Timestamp,
                            Calls = txResults.ToArray()
                        };
                        results.Add(result);
                    }
                }
            }

            return ResultWrapper<MultiCallBlockResult[]>.Success(results.ToArray());
        }

        //Returns an ordered list of blockCallsToProcess without gaps
        private static List<MultiCallBlockStateCallsModel> FillInMisingBlocks(
            MultiCallBlockStateCallsModel[] blockCallsToProcess, UInt256 from)
        {
            List<MultiCallBlockStateCallsModel>? blockCalls =
                blockCallsToProcess.OrderBy(model => model.BlockOverride.Number).ToList();

            // Get the lowest and highest numbers
            UInt256 minNumber = from;
            UInt256 maxNumber = blockCalls.Last().BlockOverride.Number;

            // Generate a sequence of numbers in that range
            IEnumerable<UInt256>? rangeNumbers = Range(minNumber, maxNumber - minNumber + 1);

            // Get the list of current numbers
            HashSet<UInt256>? currentNumbers = new HashSet<UInt256>(blockCalls.Select(m => m.BlockOverride.Number));

            // Find which numbers are missing
            IEnumerable<UInt256>? missingNumbers = rangeNumbers.Where(n => !currentNumbers.Contains(n));

            // Fill in the gaps
            foreach (UInt256 missingNumber in missingNumbers)
                blockCalls.Add(new MultiCallBlockStateCallsModel
                {
                    BlockOverride = new BlockOverride
                    {
                        Number = missingNumber,
                        GasLimit = 5_000_000,
                        FeeRecipient = Address.Zero,
                        BaseFee = 0
                    }
                });

            // Sort again after filling the gaps
            blockCalls = blockCalls.OrderBy(model => model.BlockOverride.Number).ToList();
            return blockCalls;
        }
    }

    private class EstimateGasTxExecutor : TxExecutor<UInt256?>
    {
        public EstimateGasTxExecutor(IBlockchainBridge blockchainBridge, IBlockFinder blockFinder,
            IJsonRpcConfig rpcConfig)
            : base(blockchainBridge, blockFinder, rpcConfig)
        {
        }

        protected override ResultWrapper<UInt256?> ExecuteTx(BlockHeader header, Transaction tx,
            CancellationToken token)
        {
            BlockchainBridge.CallOutput result = _blockchainBridge.EstimateGas(header, tx, token);

            if (result.Error is null) return ResultWrapper<UInt256?>.Success((UInt256)result.GasSpent);

            return result.InputError
                ? GetInputError(result)
                : ResultWrapper<UInt256?>.Fail(result.Error, ErrorCodes.ExecutionError);
        }
    }

    private class CreateAccessListTxExecutor : TxExecutor<AccessListForRpc>
    {
        private readonly bool _optimize;

        public CreateAccessListTxExecutor(IBlockchainBridge blockchainBridge, IBlockFinder blockFinder,
            IJsonRpcConfig rpcConfig, bool optimize)
            : base(blockchainBridge, blockFinder, rpcConfig)
        {
            _optimize = optimize;
        }

        protected override ResultWrapper<AccessListForRpc> ExecuteTx(BlockHeader header, Transaction tx,
            CancellationToken token)
        {
            BlockchainBridge.CallOutput result = _blockchainBridge.CreateAccessList(header, tx, token, _optimize);

            if (result.Error is null)
                return ResultWrapper<AccessListForRpc>.Success(new AccessListForRpc(GetResultAccessList(tx, result),
                    GetResultGas(tx, result)));

            return result.InputError
                ? GetInputError(result)
                : ResultWrapper<AccessListForRpc>.Fail(result.Error, ErrorCodes.ExecutionError,
                    new AccessListForRpc(GetResultAccessList(tx, result), GetResultGas(tx, result)));
        }

        private static AccessListItemForRpc[] GetResultAccessList(Transaction tx, BlockchainBridge.CallOutput result)
        {
            AccessList? accessList = result.AccessList ?? tx.AccessList;
            return accessList is null
                ? Array.Empty<AccessListItemForRpc>()
                : AccessListItemForRpc.FromAccessList(accessList);
        }

        private static UInt256 GetResultGas(Transaction transaction, BlockchainBridge.CallOutput result)
        {
            long gas = result.GasSpent;
            if (result.AccessList is not null)
            {
                // if we generated access list, we need to fix actual gas cost, as all storage was considered warm 
                gas -= IntrinsicGasCalculator.Calculate(transaction, Berlin.Instance);
                transaction.AccessList = result.AccessList;
                gas += IntrinsicGasCalculator.Calculate(transaction, Berlin.Instance);
            }

            return (UInt256)gas;
        }
    }
}
