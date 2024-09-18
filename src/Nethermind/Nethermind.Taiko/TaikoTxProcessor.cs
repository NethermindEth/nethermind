// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State;

namespace Nethermind.Taiko;

public class TaikoTransactionProcessor(
    ISpecProvider specProvider,
    IWorldState worldState,
    IVirtualMachine virtualMachine,
    ICodeInfoRepository? codeInfoRepository,
    ILogManager logManager
    ) : TransactionProcessorBase(specProvider, worldState, virtualMachine, codeInfoRepository, logManager)
{
    private readonly Address TaikoL2Address = TaikoAddressHelper.GetTaikoL2ContractAddress(specProvider);

    protected override TransactionResult ValidateStatic(Transaction tx, BlockHeader header, IReleaseSpec spec, ExecutionOptions opts,
        out long intrinsicGas)
        => base.ValidateStatic(tx, header, spec, tx.IsAnchorTx ? opts | ExecutionOptions.NoValidation : opts, out intrinsicGas);

    protected override TransactionResult BuyGas(Transaction tx, BlockHeader header, IReleaseSpec spec, ITxTracer tracer, ExecutionOptions opts,
        in UInt256 effectiveGasPrice, out UInt256 premiumPerGas, out UInt256 senderReservedGasPayment)
        => base.BuyGas(tx, header, spec, tracer, tx.IsAnchorTx ? opts | ExecutionOptions.NoValidation : opts, in effectiveGasPrice, out premiumPerGas, out senderReservedGasPayment);

    protected override long Refund(Transaction tx, BlockHeader header, IReleaseSpec spec, ExecutionOptions opts,
        in TransactionSubstate substate, in long unspentGas, in UInt256 gasPrice)
        => base.Refund(tx, header, spec, tx.IsAnchorTx ? opts | ExecutionOptions.NoValidation : opts, substate, unspentGas, gasPrice);

    protected override void PayFees(Transaction tx, BlockHeader header, IReleaseSpec spec, ITxTracer tracer, in TransactionSubstate substate, in long spentGas, in UInt256 premiumPerGas, in byte statusCode)
        => base.PayFees(tx, header, new TaikoAnchorTxReleaseSpec(spec, tx.IsAnchorTx ? null : TaikoL2Address), tracer, substate, spentGas, premiumPerGas, statusCode);

    protected override TransactionResult IncrementNonce(Transaction tx, BlockHeader header, IReleaseSpec spec, ITxTracer tracer, ExecutionOptions opts)
    {
        if (tx.IsAnchorTx)
            WorldState.CreateAccountIfNotExists(tx.SenderAddress!, UInt256.Zero, UInt256.Zero);

        return base.IncrementNonce(tx, header, spec, tracer, opts);
    }
}
