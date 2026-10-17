// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.GasPolicy;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;

namespace Nethermind.Eez.Execution;

/// <summary>
/// Executes EEZ system transactions under the chain's <see cref="EezChainRules"/>: zero fee, the chain's gas, and
/// <c>value</c> minted to the system address before the call.
/// </summary>
/// <remarks>
/// The mint happens in <see cref="PayValue"/>, which the base processor runs after it snapshots state for the
/// call, so a reverted or halted call discards the mint while the nonce increment and gas use remain.
/// </remarks>
public sealed class EezTransactionProcessor(
    EezChainRules rules,
    ITransactionProcessor.IBlobBaseFeeCalculator blobBaseFeeCalculator,
    ISpecProvider? specProvider,
    IWorldState? worldState,
    IVirtualMachine? virtualMachine,
    ICodeInfoRepository? codeInfoRepository,
    ILogManager? logManager,
    bool parallel = false)
    : EthereumTransactionProcessorBase(blobBaseFeeCalculator, specProvider, worldState, virtualMachine, codeInfoRepository, logManager, parallel)
{
    private IReleaseSpec? _feeSpecSource;
    private IReleaseSpec? _withoutFeeCollector;

    /// <remarks>
    /// The wire format carries no gas, so the budget is set here: RollupX gives what the block has left, up to its cap,
    /// which only the block being executed knows.
    /// </remarks>
    protected override TransactionResult Execute(Transaction tx, ITxTracer tracer, ExecutionOptions opts)
    {
        if (tx.IsEezSystemTransaction() && rules.SystemTransactionGas(VirtualMachine.BlockExecutionContext.Header) is { } gas)
        {
            tx.GasLimit = gas;
        }

        return base.Execute(tx, tracer, opts);
    }

    protected override TransactionResult ValidateStatic(Transaction tx, BlockHeader header, IReleaseSpec spec, ExecutionOptions opts,
        in IntrinsicGas<EthereumGasPolicy> intrinsicGas) =>
        tx.IsEezSystemTransaction() && !tx.HasSystemTransactionFields(rules)
            ? TransactionResult.ErrorType.MalformedTransaction.WithDetail("EEZ system transaction with non-protocol sender, target, gas or fee fields")
            : base.ValidateStatic(tx, header, spec, opts, in intrinsicGas);

    protected override TransactionResult BuyGas(Transaction tx, IReleaseSpec spec, ITxTracer tracer, ExecutionOptions opts,
        in UInt256 effectiveGasPrice, out UInt256 premiumPerGas, out UInt256 senderReservedGasPayment, out UInt256 blobBaseFee)
    {
        if (!tx.IsEezSystemTransaction())
        {
            return base.BuyGas(tx, spec, tracer, opts, in effectiveGasPrice, out premiumPerGas, out senderReservedGasPayment, out blobBaseFee);
        }

        premiumPerGas = UInt256.Zero;
        senderReservedGasPayment = UInt256.Zero;
        blobBaseFee = UInt256.Zero;
        return UInt256.AddOverflow(WorldState.GetBalance(tx.SenderAddress!), tx.ValueRef, out _)
            ? TransactionResult.ErrorType.MalformedTransaction.WithDetail("EEZ system transaction mint overflows the system balance")
            : TransactionResult.Ok;
    }

    protected override void PayFees(Transaction tx, BlockHeader header, IReleaseSpec spec, ITxTracer tracer,
        in TransactionSubstate substate, ulong spentGas, in UInt256 premiumPerGas, in UInt256 effectiveGasPrice,
        in UInt256 blobBaseFee, int statusCode)
    {
        if (statusCode == StatusCode.Success && spec.FeeCollector is { } collector && !spec.IsEip8037Enabled && substate.DestroyListContains(collector))
        {
            if (!ReferenceEquals(_feeSpecSource, spec))
            {
                _feeSpecSource = spec;
                _withoutFeeCollector = new WithoutFeeCollector(spec);
            }
            spec = _withoutFeeCollector!;
        }
        base.PayFees(tx, header, spec, tracer, in substate, spentGas, in premiumPerGas, in effectiveGasPrice, in blobBaseFee, statusCode);
    }

    protected override void PayValue(Transaction tx, IReleaseSpec spec, ExecutionOptions opts)
    {
        if (tx.IsEezSystemTransaction() && !tx.ValueRef.IsZero)
        {
            WorldState.AddToBalance(tx.SenderAddress!, in tx.ValueRef, spec);
        }

        base.PayValue(tx, spec, opts);
    }

    private sealed class WithoutFeeCollector(IReleaseSpec spec) : ReleaseSpecDecorator(spec)
    {
        public override Address? FeeCollector => null;
    }
}
