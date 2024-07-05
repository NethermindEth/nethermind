// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Microsoft.ClearScript.JavaScript;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.Tracing.GethStyle.Custom.JavaScript;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.State;

namespace Nethermind.Evm.TransactionProcessing;

public sealed class SystemTransactionProcessor : TransactionProcessorBase
{
    private readonly bool _isAura;

    public SystemTransactionProcessor(ISpecProvider? specProvider,
        IWorldState? worldState,
        IVirtualMachine? virtualMachine,
        ICodeInfoRepository? codeInfoRepository,
        ILogManager? logManager)
        : base(specProvider, worldState, virtualMachine, codeInfoRepository, logManager)
    {
        _isAura = SpecProvider.SealEngine == SealEngineType.AuRa;
    }

    protected override IReleaseSpec GetSpec(Transaction tx, BlockHeader header) => new SystemTransactionReleaseSpec(base.GetSpec(tx, header), _isAura);

    protected override TransactionResult ValidateGas(Transaction tx, BlockHeader header, long intrinsicGas, bool validate) => TransactionResult.Ok;

    protected override TransactionResult BuyGas(Transaction tx, BlockHeader header, IReleaseSpec spec, ITxTracer tracer, ExecutionOptions opts, in UInt256 effectiveGasPrice, out UInt256 premiumPerGas, out UInt256 senderReservedGasPayment) =>
        base.BuyGas(tx, header, spec, tracer, opts | ExecutionOptions.NoValidation, in effectiveGasPrice, out premiumPerGas, out senderReservedGasPayment);

    protected override TransactionResult IncrementNonce(Transaction tx, BlockHeader header, IReleaseSpec spec, ITxTracer tracer, ExecutionOptions opts) => TransactionResult.Ok;

    protected override void DecrementNonce(Transaction tx) { }

    protected override void PayFees(Transaction tx, BlockHeader header, IReleaseSpec spec, ITxTracer tracer, in TransactionSubstate substate, in long spentGas, in UInt256 premiumPerGas, in byte statusCode) { }

    protected override void ExecuteEvmCall(
        Transaction tx,
        BlockHeader header,
        IReleaseSpec spec,
        ITxTracer tracer,
        ExecutionOptions opts,
        in long gasAvailable,
        in ExecutionEnvironment env,
        out TransactionSubstate? substate,
        out long spentGas,
        out byte statusCode)
    {
        base.ExecuteEvmCall(tx, header, spec, tracer, opts | ExecutionOptions.NoValidation, in gasAvailable, in env, out substate, out spentGas, out statusCode);
    }

    protected override bool RecoverSenderIfNeeded(Transaction tx, IReleaseSpec spec, ExecutionOptions opts, in UInt256 effectiveGasPrice)
    {
        Address? sender = tx.SenderAddress;
        return (sender is null || (spec.IsEip158IgnoredAccount(sender) && !WorldState.AccountExists(sender)))
               && base.RecoverSenderIfNeeded(tx, spec, opts, in effectiveGasPrice);
    }
}
