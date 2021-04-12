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
                if (searchResult.IsError)
                {
                    return ResultWrapper<TResult>.Fail(searchResult);
                }

                BlockHeader header = searchResult.Object;
                if (!HasStateForBlock(_blockchainBridge, header))
                {
                    return ResultWrapper<TResult>.Fail($"No state available for block {header.Hash}",
                        ErrorCodes.ResourceUnavailable);
                }

                FixCallTx(transactionCall);

                using CancellationTokenSource cancellationTokenSource = new(_rpcConfig.Timeout);
                Transaction tx = transactionCall.ToTransaction(_blockchainBridge.GetChainId());
                return ExecuteTx(header, tx, cancellationTokenSource.Token);
            }

            protected abstract ResultWrapper<TResult> ExecuteTx(BlockHeader header, Transaction tx, CancellationToken token);

            protected ResultWrapper<TResult> GetInputError(BlockchainBridge.CallOutput result) => 
                ResultWrapper<TResult>.Fail(result.Error, ErrorCodes.InvalidInput);
            
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

        private class CallTxExecutor : TxExecutor<string>
        {
            public CallTxExecutor(IBlockchainBridge blockchainBridge, IBlockFinder blockFinder, IJsonRpcConfig rpcConfig)
                : base(blockchainBridge, blockFinder, rpcConfig)
            {
            }

            protected override ResultWrapper<string> ExecuteTx(BlockHeader header, Transaction tx, CancellationToken token)
            {
                BlockchainBridge.CallOutput result = _blockchainBridge.Call(header, tx, token);
            
                if (result.Error is null)
                {
                    return ResultWrapper<string>.Success(result.OutputData.ToHexString(true));
                }

                return result.InputError
                    ? GetInputError(result)
                    : ResultWrapper<string>.Fail("VM execution error.", ErrorCodes.ExecutionError, result.Error);
            }
        }

        private class EstimateGasTxExecutor : TxExecutor<UInt256?>
        {
            public EstimateGasTxExecutor(IBlockchainBridge blockchainBridge, IBlockFinder blockFinder, IJsonRpcConfig rpcConfig) 
                : base(blockchainBridge, blockFinder, rpcConfig)
            {
            }
            
            protected override ResultWrapper<UInt256?> ExecuteTx(BlockHeader header, Transaction tx, CancellationToken token)
            {
                BlockchainBridge.CallOutput result = _blockchainBridge.EstimateGas(header, tx, token);
            
                if (result.Error is null)
                {
                    return ResultWrapper<UInt256?>.Success((UInt256)result.GasSpent);
                }

                return result.InputError
                    ? GetInputError(result)
                    : ResultWrapper<UInt256?>.Fail(result.Error, ErrorCodes.InternalError);
            }
        }
        
        private class CreateAccessListTxExecutor : TxExecutor<AccessListForRpc>
        {
            private readonly bool _optimize;

            public CreateAccessListTxExecutor(IBlockchainBridge blockchainBridge, IBlockFinder blockFinder, IJsonRpcConfig rpcConfig, bool optimize) 
                : base(blockchainBridge, blockFinder, rpcConfig)
            {
                _optimize = optimize;
            }
            
            protected override ResultWrapper<AccessListForRpc> ExecuteTx(BlockHeader header, Transaction tx, CancellationToken token)
            {
                BlockchainBridge.CallOutput result = _blockchainBridge.CreateAccessList(header, tx, token, _optimize);
            
                if (result.Error is null)
                {
                    return ResultWrapper<AccessListForRpc>.Success(new(GetResultAccessList(tx, result), GetResultGas(tx, result)));
                }

                return result.InputError
                    ? GetInputError(result)
                    : ResultWrapper<AccessListForRpc>.Fail(result.Error, ErrorCodes.InternalError, new(GetResultAccessList(tx, result), GetResultGas(tx, result)));
            }

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
                    gas -= IntrinsicGasCalculator.Calculate(transaction, Berlin.Instance);
                    transaction.AccessList = result.AccessList;
                    gas += IntrinsicGasCalculator.Calculate(transaction, Berlin.Instance);
                }

                return (UInt256) gas;
            }
        }
    }
}
