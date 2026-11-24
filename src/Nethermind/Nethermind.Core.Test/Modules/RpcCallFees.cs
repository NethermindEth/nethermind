// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Facade;
using Nethermind.Int256;
using Nethermind.Logging;

namespace Nethermind.Core.Test.Modules;

/// <summary>
/// A transaction processor that can run a transaction without charging for its gas, built only from the processor's
/// overridable fee steps: the reference for a chain whose <c>eth_call</c> charges no fees.
/// </summary>
/// <remarks>
/// Within <see cref="BeginCall"/> on the current thread, with fees off the sender needs funds for the transferred value
/// only, and no account is debited or credited for gas; typed fee caps are still validated against the base fee. Gas
/// metering, GASPRICE and BASEFEE are unchanged. A legacy gas price below the base fee passes when its validation is off.
/// <para>
/// The rules reach the processor on the thread rather than through the processor instance, because a call's scope may
/// wrap the chain's processor in decorators of its own before <see cref="RpcCallFeeDecorator"/> sees it; a call runs
/// synchronously on the thread that starts it.
/// </para>
/// </remarks>
public class RpcCallFeeTransactionProcessor(
    ITransactionProcessor.IBlobBaseFeeCalculator blobBaseFeeCalculator,
    ISpecProvider? specProvider,
    IWorldState? worldState,
    IVirtualMachine? virtualMachine,
    ICodeInfoRepository? codeInfoRepository,
    ILogManager? logManager)
    : EthereumTransactionProcessorBase(blobBaseFeeCalculator, specProvider, worldState, virtualMachine, codeInfoRepository, logManager)
{
    [ThreadStatic] private static CallFeeRules? _callRules;

    /// <summary>Runs the transactions processed on this thread with <paramref name="chargeFees"/> and
    /// <paramref name="validateLegacyGasPrice"/> until the returned scope is disposed.</summary>
    public static IDisposable BeginCall(bool chargeFees, bool validateLegacyGasPrice)
    {
        CallFeeRules? outer = _callRules;
        _callRules = new CallFeeRules(chargeFees, validateLegacyGasPrice);
        return new Reset(outer);
    }

    /// <summary>Whether the transactions run now buy their gas, are refunded the unused part and pay the fee recipients.</summary>
    protected static bool ChargesFees => _callRules?.ChargeFees ?? true;

    /// <summary>Whether a legacy gas price below the block base fee fails the transactions run now.</summary>
    protected static bool ValidatesLegacyGasPrice => _callRules?.ValidateLegacyGasPrice ?? true;

    protected override bool TryCalculatePremiumPerGas(Transaction tx, in UInt256 baseFee, out UInt256 premiumPerGas)
    {
        if (base.TryCalculatePremiumPerGas(tx, in baseFee, out premiumPerGas)) return true;
        if (tx.Supports1559 || ValidatesLegacyGasPrice) return false;

        premiumPerGas = UInt256.Zero;
        return true;
    }

    protected override TransactionResult BuyGas(Transaction tx, IReleaseSpec spec, ITxTracer tracer, ExecutionOptions opts,
        in UInt256 effectiveGasPrice, out UInt256 premiumPerGas, out UInt256 senderReservedGasPayment, out UInt256 blobBaseFee)
    {
        if (ChargesFees)
            return base.BuyGas(tx, spec, tracer, opts, in effectiveGasPrice, out premiumPerGas, out senderReservedGasPayment, out blobBaseFee);

        premiumPerGas = senderReservedGasPayment = blobBaseFee = UInt256.Zero;
        BlockHeader header = VirtualMachine.BlockExecutionContext.Header;
        if (!TryCalculatePremiumPerGas(tx, header.BaseFeePerGas, out _))
            return TransactionResult.ErrorType.MaxFeePerGasBelowBaseFee.WithDetail("max fee per gas less than block base fee");

        if (tx.SupportsBlobs)
        {
            if (!_blobBaseFeeCalculator.TryCalculateBlobFees(header, tx, spec.BlobBaseFeeUpdateFraction, out UInt256 feePerBlobGas, out _))
                return TransactionResult.ErrorType.InsufficientMaxFeePerGasForSenderBalance.WithDetail("required balance exceeds 256 bits");
            if (tx.MaxFeePerBlobGas < feePerBlobGas)
                return TransactionResult.ErrorType.InsufficientSenderBalance.WithDetail("max fee per blob gas less than block blob gas fee");
        }

        // Gas has no monetary cost, but the sender must still fund its actual value transfer.
        return WorldState.GetBalance(tx.SenderAddress!) < tx.ValueRef ? TransactionResult.InsufficientSenderBalance : TransactionResult.Ok;
    }

    protected override void PayRefund(Transaction tx, UInt256 refundAmount, IReleaseSpec spec)
    {
        if (ChargesFees) base.PayRefund(tx, refundAmount, spec);
    }

    protected override void PayFees(Transaction tx, BlockHeader header, IReleaseSpec spec, ITxTracer tracer, in TransactionSubstate substate,
        ulong spentGas, in UInt256 premiumPerGas, in UInt256 effectiveGasPrice, in UInt256 blobBaseFee, int statusCode)
    {
        if (ChargesFees) base.PayFees(tx, header, spec, tracer, in substate, spentGas, in premiumPerGas, in effectiveGasPrice, in blobBaseFee, statusCode);
    }

    private sealed record CallFeeRules(bool ChargeFees, bool ValidateLegacyGasPrice);

    private sealed class Reset(CallFeeRules? outer) : IDisposable
    {
        public void Dispose() => _callRules = outer;
    }
}

/// <summary>Runs <c>eth_call</c> with the given fee rules on a chain whose processor is an <see cref="RpcCallFeeTransactionProcessor"/>.</summary>
/// <param name="chargeFees">Whether the call buys its gas, is refunded the unused part and pays the fee recipients.</param>
/// <param name="validateLegacyGasPrice">Whether a legacy gas price below the block base fee fails the call.</param>
/// <remarks>A typed transaction whose fee cap is below the base fee fails before execution, ahead of its intrinsic-gas check,
/// unless the system sender sends it.</remarks>
public sealed class RpcCallFeeDecorator(bool chargeFees = false, bool validateLegacyGasPrice = false) : ICallTransactionProcessorDecorator
{
    public ITransactionProcessor Decorate(ITransactionProcessor processor) => new CallProcessor(processor, chargeFees, validateLegacyGasPrice);

    private sealed class CallProcessor(ITransactionProcessor processor, bool chargeFees, bool validateLegacyGasPrice) : ITransactionProcessor
    {
        private BlockHeader? _header;

        public TransactionResult Process(Transaction transaction, ITxTracer txTracer, ExecutionOptions options)
        {
            // The system sender keeps its processor's exemption from the fee cap, as on a node without the decorator.
            if (transaction.Supports1559 && !transaction.IsSystem() && _header is not null && transaction.MaxFeePerGas < _header.BaseFeePerGas)
                return TransactionResult.ErrorType.MaxFeePerGasBelowBaseFee.WithDetail("max fee per gas less than block base fee");

            using IDisposable _ = RpcCallFeeTransactionProcessor.BeginCall(chargeFees, validateLegacyGasPrice);
            return processor.Process(transaction, txTracer, options);
        }

        public void SetBlockExecutionContext(BlockHeader blockHeader)
        {
            _header = blockHeader;
            processor.SetBlockExecutionContext(blockHeader);
        }

        public void SetBlockExecutionContext(in BlockExecutionContext blockExecutionContext)
        {
            _header = blockExecutionContext.Header;
            processor.SetBlockExecutionContext(in blockExecutionContext);
        }
    }
}
