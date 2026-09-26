// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Facade;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Int256;
using Nethermind.JsonRpc.Data;
using Nethermind.JsonRpc.Modules.Eth.GasPrice;
using Nethermind.Serialization.Rlp;

namespace Nethermind.JsonRpc.Modules.Eth
{
    //General executor
    public partial class EthRpcModule
    {
        // Single call executor
        private abstract class TxExecutor<TResult>(IBlockchainBridge blockchainBridge, IBlockFinder blockFinder, IJsonRpcConfig rpcConfig, ISpecProvider specProvider)
            : ExecutorBase<TResult, TransactionForRpc, Transaction>(blockchainBridge, blockFinder, rpcConfig)
        {
            protected bool NoBaseFee { get; set; }
            private BlockOverride? _blockOverride;
            protected BlockOverride? BlockOverride => _blockOverride;
            protected UInt256? BlobBaseFeeOverride => _blockOverride?.BlobBaseFee;

            protected BlockOverride? BlockOverrideForExecution =>
                !NoBaseFee || _blockOverride?.BaseFeePerGas is null
                    ? _blockOverride
                    : _blockOverride.WithBaseFee(UInt256.Zero);

            protected IReleaseSpec GetSpec(BlockHeader header) => specProvider.GetSpec(header);

            /// <summary>Whether a fee cap below the priority fee is rejected as input rather than left to execution.</summary>
            protected virtual bool ValidatesFeeCapOrder => true;

            protected override Result<Transaction> Prepare(TransactionForRpc call, BlockHeader header)
            {
                IReleaseSpec spec = GetSpec(header);
                Result<Transaction> result = ValidatesFeeCapOrder
                    ? call.ToValidatedTransaction(gasCap: _rpcConfig.GasCap, spec: spec)
                    : call.ToTransaction(validateUserInput: true, gasCap: _rpcConfig.GasCap, spec: spec);
                if (result.IsError) return result;

                Transaction tx = result.Data;
                tx.ChainId = _blockchainBridge.GetChainId();
                return tx;
            }

            protected override ResultWrapper<TResult> Execute(BlockHeader header, Transaction tx, Dictionary<Address, AccountOverride>? stateOverride, CancellationToken token)
            {
                if (stateOverride is not null)
                {
                    IReleaseSpec spec = specProvider.GetSpec(header);
                    foreach ((Address address, AccountOverride accountOverride) in stateOverride)
                    {
                        if (accountOverride.MovePrecompileToAddress is not null &&
                            spec.IsPrecompile(address) &&
                            accountOverride.MovePrecompileToAddress == address)
                        {
                            return ResultWrapper<TResult>.Fail(
                                $"account {address} is already overridden",
                                ErrorCodes.InvalidInput);
                        }
                    }
                }

                BlockHeader clonedHeader = header.Clone();

                if (NoBaseFee)
                {
                    clonedHeader.BaseFeePerGas = 0;
                }

                clonedHeader.GasUsed = 0;

                // The block override is applied later, inside the bridge, after the read-only state scope is opened
                // on this (base) header — so the overridden block number does not leak into state selection.
                return ExecuteTx(clonedHeader, tx, stateOverride, token);
            }

            public override ResultWrapper<TResult> Execute(
                TransactionForRpc transactionCall,
                BlockParameter? blockParameter,
                Dictionary<Address, AccountOverride>? stateOverride = null,
                SearchResult<BlockHeader>? searchResult = null)
            {
                NoBaseFee = !transactionCall.ShouldSetBaseFee();

                return base.Execute(transactionCall, blockParameter, stateOverride, searchResult);
            }

            public ResultWrapper<TResult> ExecuteTx(TransactionForRpc transactionCall, BlockParameter? blockParameter, Dictionary<Address, AccountOverride>? stateOverride = null, BlockOverride? blockOverride = null)
            {
                if (blockOverride?.GasLimit > _rpcConfig.GasCap!.Value)
                    return ResultWrapper<TResult>.Fail($"GasLimit value is too large, max value {_rpcConfig.GasCap.Value}", ErrorCodes.InvalidInput);
                _blockOverride = blockOverride;
                return Execute(transactionCall, blockParameter, stateOverride);
            }

            protected abstract ResultWrapper<TResult> ExecuteTx(BlockHeader header, Transaction tx, Dictionary<Address, AccountOverride>? stateOverride, CancellationToken token);

            protected ResultWrapper<TResult> CreateResultWrapper(bool inputError, string? errorMessage, TResult? bodyData, bool executionReverted, byte[]? executionRevertedReason)
            {
                if (inputError || errorMessage is not null)
                {
                    if (executionReverted)
                    {
                        string revertMessage = TransactionSubstate.BuildRevertMessage(executionRevertedReason, errorMessage);

                        if (executionRevertedReason is not null)
                        {
                            return ResultWrapper<TResult, string>.Fail(revertMessage, ErrorCodes.ExecutionReverted, executionRevertedReason.ToHexString(true));
                        }

                        return ResultWrapper<TResult, string?>.Fail(revertMessage, ErrorCodes.ExecutionReverted, null);
                    }

                    return bodyData is null
                        ? ResultWrapper<TResult>.Fail(errorMessage ?? "", ErrorCodes.InvalidInput)
                        : ResultWrapper<TResult>.Fail(errorMessage ?? "", ErrorCodes.InvalidInput, bodyData);
                }

                return ResultWrapper<TResult>.Success(bodyData);
            }
        }

        private class CallTxExecutor(IBlockchainBridge blockchainBridge, IBlockFinder blockFinder, IJsonRpcConfig rpcConfig, ISpecProvider specProvider)
            : TxExecutor<HexBytes>(blockchainBridge, blockFinder, rpcConfig, specProvider)
        {
            protected override bool ValidatesFeeCapOrder => false;

            protected override ResultWrapper<HexBytes> ExecuteTx(BlockHeader header, Transaction tx, Dictionary<Address, AccountOverride>? stateOverride, CancellationToken token)
            {
                CallOutput result = _blockchainBridge.Call(header, tx, stateOverride, BlobBaseFeeOverride, BlockOverrideForExecution, token);

                if (!result.ExecutionReverted && result.Error is not null)
                {
                    string message = result.InputError
                        ? ErrorWrapper.EthCall(result.Error, tx.GasLimit)
                        : result.Error;
                    return ResultWrapper<HexBytes>.Fail(message, ErrorCodes.ExecutionError);
                }

                HexBytes outputData = result.OutputData is null ? default : new HexBytes(result.OutputData);
                return CreateResultWrapper(result.InputError, result.Error, outputData, result.ExecutionReverted, result.OutputData);
            }
        }

        private class EstimateGasTxExecutor(IBlockchainBridge blockchainBridge, IBlockFinder blockFinder, IJsonRpcConfig rpcConfig, ISpecProvider specProvider)
            : TxExecutor<UInt256?>(blockchainBridge, blockFinder, rpcConfig, specProvider)
        {
            private readonly int _errorMargin = rpcConfig.EstimateErrorMargin;

            protected override bool ValidatesFeeCapOrder => false;

            public override ResultWrapper<UInt256?> Execute(
                TransactionForRpc transactionCall,
                BlockParameter? blockParameter,
                Dictionary<Address, AccountOverride>? stateOverride = null,
                SearchResult<BlockHeader>? searchResult = null)
            {
                // Match Geth: eth_estimateGas treats gas: 0x0 the same as an omitted gas field and
                // bounds the binary search by blockGasLimit (then caps at gasCap inside ToTransaction).
                if (!transactionCall.Gas.IsGasCapped())
                {
                    if (BlockOverride?.GasLimit is not null)
                    {
                        transactionCall.Gas = BlockOverride.GasLimit.Value;
                    }
                    else
                    {
                        searchResult ??= _blockFinder.SearchForHeader(blockParameter);
                        if (!searchResult.Value.IsError)
                            transactionCall.Gas = searchResult.Value.Object!.GasLimit;
                    }
                }
                return base.Execute(transactionCall, blockParameter, stateOverride, searchResult);
            }

            protected override ResultWrapper<UInt256?> ExecuteTx(BlockHeader header, Transaction tx, Dictionary<Address, AccountOverride> stateOverride, CancellationToken token)
            {
                CallOutput result = _blockchainBridge.EstimateGas(header, tx, _errorMargin, stateOverride, BlobBaseFeeOverride, BlockOverrideForExecution, _rpcConfig.GasCap ?? 0, token);

                // A transaction rejected before execution reports, as GasSpent, the gas limit it was rejected at.
                string? errorMessage = result.InputError
                    ? ErrorWrapper.EstimateGasBinarySearch(result.Error!, result.GasSpent)
                    : result.Error;

                return CreateResultWrapper(result.InputError, errorMessage, errorMessage is null ? (UInt256)result.GasSpent : null, result.ExecutionReverted, result.OutputData);
            }
        }

        private class CreateAccessListTxExecutor(IBlockchainBridge blockchainBridge, IBlockFinder blockFinder, IJsonRpcConfig rpcConfig, ISpecProvider specProvider, IGasPriceOracle gasPriceOracle, bool optimize)
            : TxExecutor<AccessListResultForRpc?>(blockchainBridge, blockFinder, rpcConfig, specProvider)
        {
            private BigInteger? _feeCapBeyond256Bits;
            private string? _feeDefaultsError;

            protected override bool ValidatesFeeCapOrder => false;

            /// <remarks>
            /// The fee fields follow the defaults a transaction about to be sent gets, so a malformed pair is reported
            /// with its values in hexadecimal, as the request carried them, rather than failing where the fees are
            /// applied. A request that sets one of the fee cap and priority fee after London gets the other before it
            /// is priced: a missing priority fee is the node's suggested one, and a missing fee cap is the priority fee
            /// plus twice the block's base fee. A request with no fee field at all stays unpriced, so a sender that
            /// cannot afford fees the node would pick can still get its access list.
            /// </remarks>
            public override ResultWrapper<AccessListResultForRpc?> Execute(
                TransactionForRpc transactionCall,
                BlockParameter? blockParameter,
                Dictionary<Address, AccountOverride>? stateOverride = null,
                SearchResult<BlockHeader>? searchResult = null)
            {
                searchResult ??= _blockFinder.SearchForHeader(blockParameter);
                if (!searchResult.Value.IsError)
                    FillFeeDefaults(transactionCall, searchResult.Value.Object!);

                return base.Execute(transactionCall, blockParameter, stateOverride, searchResult);
            }

            protected override Result<Transaction> Prepare(TransactionForRpc call, BlockHeader header) =>
                _feeDefaultsError is { } feeDefaultsError ? feeDefaultsError : base.Prepare(call, header);

            private void FillFeeDefaults(TransactionForRpc call, BlockHeader header)
            {
                bool isLondon = GetSpec(header).IsEip1559Enabled;
                _feeDefaultsError = FeeDefaultRules.Error(call, isLondon);

                // Before London the rules above leave no fee field to fill.
                if (_feeDefaultsError is not null
                    || !isLondon
                    || call is not EIP1559TransactionForRpc { GasPrice: null } request
                    || request.MaxFeePerGas is null == request.MaxPriorityFeePerGas is null)
                {
                    return;
                }

                UInt256 priorityFee = request.MaxPriorityFeePerGas ??= gasPriceOracle.GetMaxPriorityGasFeeEstimate();
                if (request.MaxFeePerGas is { } feeCap)
                {
                    _feeDefaultsError = feeCap < priorityFee
                        ? $"maxFeePerGas ({feeCap.ToHexString(skipLeadingZeros: true)}) < maxPriorityFeePerGas ({priorityFee.ToHexString(skipLeadingZeros: true)})"
                        : null;
                    return;
                }

                // The fee cap is filled without a bound and applied as its low 256 bits, which then sit below the
                // priority fee; the error names the transaction with the fee cap it was filled with.
                BigInteger filledFeeCap = (BigInteger)priorityFee + (BigInteger)header.BaseFeePerGas * 2;
                request.MaxFeePerGas = (UInt256)(filledFeeCap & (BigInteger)UInt256.MaxValue);
                if (filledFeeCap > (BigInteger)UInt256.MaxValue && call.GetType() == typeof(EIP1559TransactionForRpc))
                    _feeCapBeyond256Bits = filledFeeCap;
            }

            /// <summary>The hash of an unsigned dynamic-fee <paramref name="tx"/> carrying <paramref name="feeCap"/>.</summary>
            private static Hash256 HashWithFeeCap(Transaction tx, BigInteger feeCap)
            {
                Rlp payload = Rlp.Encode(
                    Rlp.Encode(tx.ChainId ?? 0UL),
                    Rlp.Encode(tx.Nonce),
                    Rlp.Encode(tx.MaxPriorityFeePerGas),
                    Rlp.Encode(feeCap),
                    Rlp.Encode(tx.GasLimit),
                    tx.To is null ? Rlp.OfEmptyByteArray : Rlp.Encode(tx.To.Bytes),
                    Rlp.Encode(tx.Value),
                    Rlp.Encode(tx.Data.Span),
                    Rlp.Encode(tx.AccessList ?? AccessList.Empty),
                    Rlp.OfEmptyByteArray,
                    Rlp.OfEmptyByteArray,
                    Rlp.OfEmptyByteArray);
                return Keccak.Compute([(byte)TxType.EIP1559, .. payload.Bytes]);
            }

            protected override ResultWrapper<AccessListResultForRpc?> ExecuteTx(BlockHeader header, Transaction tx, Dictionary<Address, AccountOverride> stateOverride, CancellationToken token)
            {
                CallOutput result = _blockchainBridge.CreateAccessList(header, tx, stateOverride, optimize, BlobBaseFeeOverride, token);

                AccessListResultForRpc rpcAccessListResult = new(
                    accessList: AccessListForRpc.FromAccessList(result.AccessList ?? tx.AccessList),
                    gasUsed: (UInt256)result.GasSpent,
                    result.Error);

                if (result.InputError)
                {
                    Hash256 txHash = _feeCapBeyond256Bits is { } feeCap ? HashWithFeeCap(tx, feeCap) : tx.Hash ?? tx.CalculateHash();
                    string wrapped = ErrorWrapper.CreateAccessList(result.Error!, txHash);
                    return ResultWrapper<AccessListResultForRpc?>.Fail(wrapped, ErrorCodes.InvalidInput);
                }
                return ResultWrapper<AccessListResultForRpc?>.Success(rpcAccessListResult);
            }
        }
    }
}
