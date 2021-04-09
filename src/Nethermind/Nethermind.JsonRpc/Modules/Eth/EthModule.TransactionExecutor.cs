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

using System;
using System.Threading;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Extensions;
using Nethermind.Evm;
using Nethermind.Facade;
using Nethermind.Int256;
using Nethermind.JsonRpc.Data;
using Nethermind.Specs.Forks;

namespace Nethermind.JsonRpc.Modules.Eth
{
    public partial class EthModule
    {
        private abstract class TransactionExecutor<T>
        {
            protected readonly IBlockchainBridge _blockchainBridge;
            private readonly IBlockFinder _blockFinder;
            private readonly TimeSpan _cancellationTokenTimeout;
            private readonly IJsonRpcConfig _rpcConfig;

            protected TransactionExecutor(IBlockchainBridge blockchainBridge, IBlockFinder blockFinder, TimeSpan cancellationTokenTimeout, IJsonRpcConfig rpcConfig)
            {
                _blockchainBridge = blockchainBridge;
                _blockFinder = blockFinder;
                _cancellationTokenTimeout = cancellationTokenTimeout;
                _rpcConfig = rpcConfig;
            }
            
            public ResultWrapper<T> ExecuteTx(
                TransactionForRpc transactionCall, 
                BlockParameter? blockParameter)
            {
                SearchResult<BlockHeader> searchResult = _blockFinder.SearchForHeader(blockParameter);
                if (searchResult.IsError)
                {
                    return ResultWrapper<T>.Fail(searchResult);
                }

                BlockHeader header = searchResult.Object;
                if (!HasStateForBlock(_blockchainBridge, header))
                {
                    return ResultWrapper<T>.Fail($"No state available for block {header.Hash}",
                        ErrorCodes.ResourceUnavailable);
                }

                FixCallTx(transactionCall);

                using CancellationTokenSource cancellationTokenSource = new(_cancellationTokenTimeout);
                Transaction tx = transactionCall.ToTransaction(_blockchainBridge.GetChainId());
                BlockchainBridge.CallOutput result = ExecuteTx(header, tx, cancellationTokenSource.Token);

                if (result.Error == null)
                {
                    return ResultWrapper<T>.Success(GetResult(tx, result));
                }

                return result.InputError
                    ? GetInputError(result)
                    : GetExecutionError(tx, result);
            }

            private ResultWrapper<T> GetInputError(BlockchainBridge.CallOutput result) => 
                ResultWrapper<T>.Fail(result.Error, ErrorCodes.InvalidInput);

            protected abstract BlockchainBridge.CallOutput ExecuteTx(BlockHeader header, Transaction tx, CancellationToken token);
            
            protected abstract T GetResult(Transaction tx, BlockchainBridge.CallOutput result);
            
            protected abstract ResultWrapper<T> GetExecutionError(Transaction tx, BlockchainBridge.CallOutput result);

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

        private class CallTransactionExecutor : TransactionExecutor<string>
        {
            public CallTransactionExecutor(IBlockchainBridge blockchainBridge, IBlockFinder blockFinder, TimeSpan cancellationTokenTimeout, IJsonRpcConfig rpcConfig)
                : base(blockchainBridge, blockFinder, cancellationTokenTimeout, rpcConfig)
            {
            }

            protected override ResultWrapper<string> GetExecutionError(Transaction tx, BlockchainBridge.CallOutput result) => 
                ResultWrapper<string>.Fail("VM execution error.", ErrorCodes.ExecutionError, result.Error);

            protected override string GetResult(Transaction tx, BlockchainBridge.CallOutput result) => 
                result.OutputData.ToHexString(true);

            protected override BlockchainBridge.CallOutput ExecuteTx(BlockHeader header, Transaction tx, CancellationToken token) => 
                _blockchainBridge.Call(header, tx, token);
        }

        private class EstimateGasTransactionExecutor : TransactionExecutor<UInt256?>
        {
            public EstimateGasTransactionExecutor(IBlockchainBridge blockchainBridge, IBlockFinder blockFinder, TimeSpan cancellationTokenTimeout, IJsonRpcConfig rpcConfig) 
                : base(blockchainBridge, blockFinder, cancellationTokenTimeout, rpcConfig)
            {
            }

            protected override BlockchainBridge.CallOutput ExecuteTx(BlockHeader header, Transaction tx, CancellationToken token) => 
                _blockchainBridge.EstimateGas(header, tx, token);

            protected override UInt256? GetResult(Transaction tx, BlockchainBridge.CallOutput result) => (UInt256)result.GasSpent;

            protected override ResultWrapper<UInt256?> GetExecutionError(Transaction tx, BlockchainBridge.CallOutput result) => 
                ResultWrapper<UInt256?>.Fail(result.Error, ErrorCodes.InternalError);
        }
        
        private class CreateAccessListTransactionExecutor : TransactionExecutor<AccessListForRpc>
        {
            private readonly bool _optimize;

            public CreateAccessListTransactionExecutor(IBlockchainBridge blockchainBridge, IBlockFinder blockFinder, TimeSpan cancellationTokenTimeout, IJsonRpcConfig rpcConfig, bool optimize) 
                : base(blockchainBridge, blockFinder, cancellationTokenTimeout, rpcConfig)
            {
                _optimize = optimize;
            }

            protected override BlockchainBridge.CallOutput ExecuteTx(BlockHeader header, Transaction tx, CancellationToken token) => 
                _blockchainBridge.CreateAccessList(header, tx, token, _optimize);

            protected override AccessListForRpc GetResult(Transaction tx, BlockchainBridge.CallOutput result) =>
                new(GetResultAccessList(tx, result), GetResultGas(tx, result));
            

            protected override ResultWrapper<AccessListForRpc> GetExecutionError(Transaction tx, BlockchainBridge.CallOutput result) => 
                ResultWrapper<AccessListForRpc>.Fail(result.Error, ErrorCodes.InternalError, new(GetResultAccessList(tx, result), GetResultGas(tx, result)));
            
            private static AccessListItemForRpc[] GetResultAccessList(Transaction tx, BlockchainBridge.CallOutput result)
            {
                AccessList? accessList = result.AccessList ?? tx.AccessList;
                return accessList is null ? Array.Empty<AccessListItemForRpc>() : AccessListItemForRpc.FromAccessList(accessList);
            }
            
            private static UInt256 GetResultGas(Transaction transaction, BlockchainBridge.CallOutput result)
            {
                long gas = result.GasSpent;
                if (result.AccessList is not null)
                {
                    // if we generated access list, we need to fix actual gas cost, as all storage was considered warm 
                    gas = gas - IntrinsicGasCalculator.AccessListCost(transaction.AccessList, Berlin.Instance)
                          + IntrinsicGasCalculator.AccessListCost(result.AccessList, Berlin.Instance);
                }

                return (UInt256) gas;
            }
        }
    }
}
