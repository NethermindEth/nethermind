// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.State;

namespace Nethermind.Evm.TransactionProcessing;

public sealed class SystemTransactionProcessor : TransactionProcessorBase
{
    private readonly bool _isAura;

    /// <summary>
    /// Hacky flag to execution options, to pass information how original validate should behave.
    /// Needed to decide if we need to subtract transaction value.
    /// </summary>
    private const int OriginalValidate = 2 << 30;

    public SystemTransactionProcessor(ISpecProvider? specProvider,
        IWorldState? worldState,
        IVirtualMachine? virtualMachine,
        ICodeInfoRepository? codeInfoRepository,
        ILogManager? logManager)
        : base(specProvider, worldState, virtualMachine, codeInfoRepository, logManager)
    {
        _isAura = SpecProvider.SealEngine == SealEngineType.AuRa;
    }

    protected override TransactionResult Execute(Transaction tx, in BlockExecutionContext blCtx, ITxTracer tracer, ExecutionOptions opts)
    {
        if (_isAura && !blCtx.IsGenesis)
        {
            WorldState.CreateAccountIfNotExists(Address.SystemUser, UInt256.Zero, UInt256.Zero);
        }

        return base.Execute(tx, in blCtx, tracer, (opts != ExecutionOptions.SkipValidation && !opts.HasFlag(ExecutionOptions.SkipValidationAndCommit))
            ? opts | (ExecutionOptions)OriginalValidate | ExecutionOptions.SkipValidationAndCommit
            : opts);
    }


    protected override IReleaseSpec GetSpec(Transaction tx, BlockHeader header) => new SystemTransactionReleaseSpec(base.GetSpec(tx, header), _isAura, header.IsGenesis);

    protected override TransactionResult ValidateGas(Transaction tx, BlockHeader header, long minGasRequired, bool validate) => TransactionResult.Ok;

    protected override TransactionResult IncrementNonce(Transaction tx, BlockHeader header, IReleaseSpec spec, ITxTracer tracer, ExecutionOptions opts) => TransactionResult.Ok;

    protected override void DecrementNonce(Transaction tx) { }

    protected override void PayFees(Transaction tx, BlockHeader header, IReleaseSpec spec, ITxTracer tracer, in TransactionSubstate substate, in long spentGas, in UInt256 premiumPerGas, in UInt256 blobBaseFee, in byte statusCode) { }

    protected override void PayValue(Transaction tx, IReleaseSpec spec, ExecutionOptions opts)
    {
        if (opts.HasFlag((ExecutionOptions)OriginalValidate))
        {
            base.PayValue(tx, spec, opts);
        }
    }

    protected override IntrinsicGas CalculateIntrinsicGas(Transaction tx, IReleaseSpec spec) => tx is SystemCall ? default : base.CalculateIntrinsicGas(tx, spec);

    protected override bool RecoverSenderIfNeeded(Transaction tx, IReleaseSpec spec, ExecutionOptions opts, in UInt256 effectiveGasPrice)
    {
        Address? sender = tx.SenderAddress;
        return (sender is null || (spec.IsEip158IgnoredAccount(sender) && !WorldState.AccountExists(sender)))
               && base.RecoverSenderIfNeeded(tx, spec, opts, in effectiveGasPrice);
    }
}
