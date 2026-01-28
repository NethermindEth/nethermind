// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.GasPolicy;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Evm.State;

namespace Nethermind.Evm.TransactionProcessing;

public sealed class SystemTransactionProcessor<TGasPolicy> : TransactionProcessorBase<TGasPolicy>
    where TGasPolicy : struct, IGasPolicy<TGasPolicy>
{
    private readonly bool _isAura;

    /// <summary>
    /// Hacky flag to execution options, to pass information how original validate should behave.
    /// Needed to decide if we need to subtract transaction value.
    /// </summary>
    private const int OriginalValidate = 2 << 30;

    public SystemTransactionProcessor(
        ITransactionProcessor.IBlobBaseFeeCalculator blobBaseFeeCalculator,
        ISpecProvider? specProvider,
        IWorldState? worldState,
        IVirtualMachine<TGasPolicy>? virtualMachine,
        ICodeInfoRepository? codeInfoRepository,
        ILogManager? logManager)
        : base(blobBaseFeeCalculator, specProvider, worldState, virtualMachine, codeInfoRepository, logManager)
    {
        _isAura = SpecProvider.SealEngine == SealEngineType.AuRa;
    }

    protected override TransactionResult Execute<TLogTracing>(Transaction tx, ITxTracer tracer, ExecutionOptions opts)
    {
        if (_isAura && !VirtualMachine.BlockExecutionContext.IsGenesis)
        {
            WorldState.CreateAccountIfNotExists(Address.SystemUser, UInt256.Zero, UInt256.Zero);
        }

        return base.Execute<TLogTracing>(tx, tracer, (opts != ExecutionOptions.SkipValidation && !opts.HasFlag(ExecutionOptions.SkipValidationAndCommit))
            ? opts | (ExecutionOptions)OriginalValidate | ExecutionOptions.SkipValidationAndCommit
            : opts);
    }

    protected override TransactionResult BuyGas<TLogTracing>(Transaction tx, IReleaseSpec spec, ITxTracer tracer, ExecutionOptions opts,
        in UInt256 effectiveGasPrice, out UInt256 premiumPerGas, out UInt256 senderReservedGasPayment,
        out UInt256 blobBaseFee)
    {
        premiumPerGas = 0;
        senderReservedGasPayment = 0;
        blobBaseFee = 0;
        return TransactionResult.Ok;
    }

    protected override IReleaseSpec GetSpec(BlockHeader header) => SystemTransactionReleaseSpec.GetReleaseSpec(base.GetSpec(header), _isAura, header.IsGenesis);

    protected override TransactionResult ValidateGas<TLogTracing>(Transaction tx, BlockHeader header, long minGasRequired) => TransactionResult.Ok;

    protected override TransactionResult IncrementNonce<TLogTracing>(Transaction tx, BlockHeader header, IReleaseSpec spec, ITxTracer tracer, ExecutionOptions opts) => TransactionResult.Ok;

    protected override void DecrementNonce(Transaction tx) { }

    protected override void PayFees(Transaction tx, BlockHeader header, IReleaseSpec spec, ITxTracer tracer, in TransactionSubstate substate, long spentGas, in UInt256 premiumPerGas, in UInt256 blobBaseFee, int statusCode) { }

    protected override void PayValue(Transaction tx, IReleaseSpec spec, ExecutionOptions opts)
    {
        if (opts.HasFlag((ExecutionOptions)OriginalValidate))
        {
            base.PayValue(tx, spec, opts);
        }
    }

    protected override IntrinsicGas<TGasPolicy> CalculateIntrinsicGas(Transaction tx, IReleaseSpec spec) => tx is SystemCall ? default : base.CalculateIntrinsicGas(tx, spec);

    protected override bool RecoverSenderIfNeeded<TLogTracing>(Transaction tx, IReleaseSpec spec, ExecutionOptions opts, in UInt256 effectiveGasPrice)
    {
        Address? sender = tx.SenderAddress;
        return (sender is null || (spec.IsEip158IgnoredAccount(sender) && !WorldState.AccountExists(sender)))
               && base.RecoverSenderIfNeeded<TLogTracing>(tx, spec, opts, in effectiveGasPrice);
    }

    protected override void PayRefund(Transaction tx, in UInt256 refundAmount, IReleaseSpec spec) { }
}
