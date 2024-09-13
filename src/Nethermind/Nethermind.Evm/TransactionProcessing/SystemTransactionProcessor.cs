// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
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
        IVirtualMachine? virtualMachine,
        ICodeInfoRepository? codeInfoRepository,
        ILogManager? logManager)
        : base(specProvider, virtualMachine, codeInfoRepository, logManager)
    {
        _isAura = SpecProvider.SealEngine == SealEngineType.AuRa;
    }

    protected override TransactionResult Execute(IWorldState worldState, Transaction tx, in BlockExecutionContext blCtx, ITxTracer tracer, ExecutionOptions opts)
    {
        if (_isAura && !blCtx.Header.IsGenesis)
        {
            WorldState.CreateAccountIfNotExists(Address.SystemUser, UInt256.Zero, UInt256.Zero);
        }

        return base.Execute(worldState, tx, in blCtx, tracer, !opts.HasFlag(ExecutionOptions.NoValidation)
            ? opts | (ExecutionOptions)OriginalValidate | ExecutionOptions.NoValidation
            : opts);
    }


    protected override IReleaseSpec GetSpec(Transaction tx, BlockHeader header) => new SystemTransactionReleaseSpec(base.GetSpec(tx, header), _isAura, header.IsGenesis);

    protected override TransactionResult ValidateGas(Transaction tx, BlockHeader header, long intrinsicGas, bool validate) => TransactionResult.Ok;

    protected override TransactionResult IncrementNonce(IWorldState worldState, Transaction tx, BlockHeader header, IReleaseSpec spec, ITxTracer tracer, ExecutionOptions opts) => TransactionResult.Ok;

    protected override void DecrementNonce(Transaction tx) { }

    protected override void PayFees(IWorldState worldState, Transaction tx, BlockHeader header, IReleaseSpec spec, ITxTracer tracer, in TransactionSubstate substate, in long spentGas, in UInt256 premiumPerGas, in byte statusCode) { }

    protected override void PayValue(Transaction tx, IReleaseSpec spec, ExecutionOptions opts)
    {
        if (opts.HasFlag((ExecutionOptions)OriginalValidate))
        {
            base.PayValue(tx, spec, opts);
        }
    }

    protected override bool RecoverSenderIfNeeded(IWorldState worldState, Transaction tx, IReleaseSpec spec, ExecutionOptions opts, in UInt256 effectiveGasPrice)
    {
        Address? sender = tx.SenderAddress;
        return (sender is null || (spec.IsEip158IgnoredAccount(sender) && !WorldState.AccountExists(sender)))
               && base.RecoverSenderIfNeeded(worldState, tx, spec, opts, in effectiveGasPrice);
    }
}
