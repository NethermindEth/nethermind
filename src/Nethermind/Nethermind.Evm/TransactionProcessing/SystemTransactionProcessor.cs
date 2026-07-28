// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.GasPolicy;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Evm.State;
using System;

namespace Nethermind.Evm.TransactionProcessing;

public class SystemTransactionProcessor<TGasPolicy>(
    ITransactionProcessor.IBlobBaseFeeCalculator blobBaseFeeCalculator,
    ISpecProvider? specProvider,
    IWorldState? worldState,
    IVirtualMachine<TGasPolicy>? virtualMachine,
    ICodeInfoRepository? codeInfoRepository,
    ILogManager? logManager)
    : TransactionProcessorBase<TGasPolicy>(blobBaseFeeCalculator, specProvider, worldState, virtualMachine, codeInfoRepository, logManager)
    where TGasPolicy : struct, IGasPolicy<TGasPolicy>
{
    // Set in Execute and read within the same synchronous base.Execute -> PayValue chain (system txs don't recurse), so a plain field needs no synchronization.
    private bool _payOriginalValue;

    /// <summary>
    /// Whether to suppress BAL reads of the SYSTEM_ADDRESS account for this transaction.
    /// EIP-7928 excludes the SYSTEM_ADDRESS caller from BALs for system contract calls;
    /// engines that surface the system user (e.g. AuRa) override to return false.
    /// </summary>
    protected virtual bool ShouldSuppressSystemAccountReads(Transaction tx) =>
        tx.SenderAddress == Address.SystemUser;

    /// <summary>
    /// Hook for consensus-specific pre-execution state setup. Default is a no-op; subclasses
    /// (e.g. AuRa) materialise SYSTEM_ADDRESS so the BAL records the access.
    /// </summary>
    protected virtual void OnBeforeSystemTransaction() { }

    /// <summary>
    /// Whether <see cref="GetSpec"/> should skip wrapping the spec for the system transaction and
    /// return it unwrapped. Default returns <c>header.IsGenesis</c> so genesis system transactions
    /// run with EIP-158 still on. Subclasses override to force EIP-158 off even at genesis.
    /// </summary>
    /// <remarks>
    /// This is a distinct decision from any block-execution "is genesis" flag used elsewhere (e.g.
    /// pre-execution account materialisation); the two are intentionally decoupled — do not unify them.
    /// </remarks>
    protected virtual bool SkipSystemSpecWrap(BlockHeader header) => header.IsGenesis;

    protected override TransactionResult Execute(Transaction tx, ITxTracer tracer, ExecutionOptions opts)
    {
        using IDisposable? systemAccountReadSuppression = ShouldSuppressSystemAccountReads(tx) ? WorldState.BeginSystemAccountReadSuppression() : null;

        OnBeforeSystemTransaction();

        ExecutionOptions coreOpts = opts & ~ExecutionOptions.Warmup;
        _payOriginalValue = (coreOpts & ExecutionOptions.SkipValidation) != ExecutionOptions.SkipValidation
                            && !coreOpts.HasFlag(ExecutionOptions.SkipValidationAndCommit);
        return base.Execute(tx, tracer, _payOriginalValue ? opts | ExecutionOptions.SkipValidationAndCommit : opts);
    }

    protected override TransactionResult BuyGas(Transaction tx, IReleaseSpec spec, ITxTracer tracer, ExecutionOptions opts,
        in UInt256 effectiveGasPrice, out UInt256 premiumPerGas, out UInt256 senderReservedGasPayment,
        out UInt256 blobBaseFee)
    {
        premiumPerGas = 0;
        senderReservedGasPayment = 0;
        blobBaseFee = 0;
        return TransactionResult.Ok;
    }

    protected override IReleaseSpec GetSpec(BlockHeader header) =>
        base.GetSpec(header).ForSystemTransaction(SkipSystemSpecWrap(header));

    protected override TransactionResult ValidateGas(Transaction tx, BlockHeader header, IReleaseSpec spec, in TGasPolicy intrinsicGas, ulong minGasRequired, bool validate) => TransactionResult.Ok;

    protected override TransactionResult IncrementNonce(Transaction tx, BlockHeader header, IReleaseSpec spec, ITxTracer tracer, ExecutionOptions opts) => TransactionResult.Ok;

    protected override void DecrementNonce(Transaction tx) { }

    protected override void PayFees(Transaction tx, BlockHeader header, IReleaseSpec spec, ITxTracer tracer, in TransactionSubstate substate, ulong spentGas, in UInt256 premiumPerGas, in UInt256 effectiveGasPrice, in UInt256 blobBaseFee, int statusCode) { }

    protected override void PayValue(Transaction tx, IReleaseSpec spec, ExecutionOptions opts)
    {
        if (_payOriginalValue)
        {
            base.PayValue(tx, spec, opts);
        }
    }

    protected override IntrinsicGas<TGasPolicy> CalculateIntrinsicGas(Transaction tx, IReleaseSpec spec, ulong blockGasLimit)
    {
        if (tx is not SystemCall)
        {
            return base.CalculateIntrinsicGas(tx, spec, blockGasLimit);
        }

        TGasPolicy intrinsicGas = TGasPolicy.CreateSystemTransactionIntrinsicGas(blockGasLimit);
        return new IntrinsicGas<TGasPolicy>(intrinsicGas, intrinsicGas);
    }

    protected override TransactionResult CalculateAvailableGas(Transaction tx, IReleaseSpec spec, in IntrinsicGas<TGasPolicy> intrinsicGas, out TGasPolicy gasAvailable)
    {
        if (tx is SystemCall)
        {
            gasAvailable = TGasPolicy.CreateSystemTransactionAvailableGas(tx.GasLimit, intrinsicGas.Standard, spec);
            return TransactionResult.Ok;
        }

        return base.CalculateAvailableGas(tx, spec, in intrinsicGas, out gasAvailable);
    }

    protected override bool RecoverSenderIfNeeded(Transaction tx, IReleaseSpec spec, ExecutionOptions opts, in UInt256 effectiveGasPrice)
    {
        Address? sender = tx.SenderAddress;
        return (sender is null || (sender == spec.Eip158IgnoredAccount && !WorldState.AccountExists(sender)))
               && base.RecoverSenderIfNeeded(tx, spec, opts, in effectiveGasPrice);
    }

    protected override void PayRefund(Transaction tx, UInt256 refundAmount, IReleaseSpec spec) { }
}
