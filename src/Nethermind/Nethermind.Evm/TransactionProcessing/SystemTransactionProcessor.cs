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
    /// <summary>
    /// Hacky flag to execution options, to pass information how original validate should behave.
    /// Needed to decide if we need to subtract transaction value.
    /// </summary>
    protected const int OriginalValidate = 2 << 30;

    // SystemTransactionProcessor instances are created lazily by the base TransactionProcessor
    // (including BAL pool workers) regardless of consensus engine, so AuRa behaviour cannot be
    // applied only by an AuRaSystemTransactionProcessor subclass — we must detect AuRa here.
    private readonly bool _isAura = specProvider?.SealEngine == SealEngineType.AuRa;

    /// <summary>
    /// Whether to suppress reads of the SYSTEM_ADDRESS account in the BAL for this transaction.
    /// </summary>
    /// <remarks>
    /// EIP-7928 excludes the SYSTEM_ADDRESS caller from BALs for system contract calls. AuRa
    /// surfaces SYSTEM_ADDRESS reads so its system contracts behave consistently with the
    /// AuRa-specific BAL accounting; non-AuRa engines suppress.
    /// </remarks>
    protected virtual bool ShouldSuppressSystemAccountReads(Transaction tx) =>
        !_isAura && tx.SenderAddress == Address.SystemUser;

    /// <summary>
    /// Hook for consensus-specific pre-execution state setup. AuRa materialises the SYSTEM_ADDRESS
    /// account on every non-genesis system call so the BAL records the create event.
    /// </summary>
    protected virtual void OnBeforeSystemTransaction()
    {
        if (_isAura && !VirtualMachine.BlockExecutionContext.IsGenesis)
        {
            WorldState.CreateAccountIfNotExists(Address.SystemUser, UInt256.Zero, UInt256.Zero);
        }
    }

    protected override TransactionResult Execute(Transaction tx, ITxTracer tracer, ExecutionOptions opts)
    {
        using IDisposable? systemAccountReadSuppression = ShouldSuppressSystemAccountReads(tx) ? WorldState.BeginSystemAccountReadSuppression() : null;

        OnBeforeSystemTransaction();

        ExecutionOptions coreOpts = opts & ~ExecutionOptions.Warmup;
        return base.Execute(tx, tracer, ((coreOpts & ExecutionOptions.SkipValidation) != ExecutionOptions.SkipValidation && !coreOpts.HasFlag(ExecutionOptions.SkipValidationAndCommit))
            ? opts | (ExecutionOptions)OriginalValidate | ExecutionOptions.SkipValidationAndCommit
            : opts);
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

    // AuRa system transactions always wrap the spec (EIP-158 disabled at genesis), matching
    // master's pre-PR behaviour; other engines short-circuit through the genesis fast path.
    protected override IReleaseSpec GetSpec(BlockHeader header) =>
        base.GetSpec(header).ForSystemTransaction(_isAura ? false : header.IsGenesis);

    protected override TransactionResult ValidateGas(Transaction tx, BlockHeader header, IReleaseSpec spec, in TGasPolicy intrinsicGas, long minGasRequired, bool validate) => TransactionResult.Ok;

    protected override TransactionResult IncrementNonce(Transaction tx, BlockHeader header, IReleaseSpec spec, ITxTracer tracer, ExecutionOptions opts) => TransactionResult.Ok;

    protected override void DecrementNonce(Transaction tx) { }

    protected override void PayFees(Transaction tx, BlockHeader header, IReleaseSpec spec, ITxTracer tracer, in TransactionSubstate substate, long spentGas, in UInt256 premiumPerGas, in UInt256 blobBaseFee, int statusCode) { }

    protected override void PayValue(Transaction tx, IReleaseSpec spec, ExecutionOptions opts)
    {
        if (opts.HasFlag((ExecutionOptions)OriginalValidate))
        {
            base.PayValue(tx, spec, opts);
        }
    }

    protected override IntrinsicGas<TGasPolicy> CalculateIntrinsicGas(Transaction tx, IReleaseSpec spec, long blockGasLimit)
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
