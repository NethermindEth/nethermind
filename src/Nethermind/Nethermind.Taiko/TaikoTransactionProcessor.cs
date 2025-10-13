// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Evm.State;
using Nethermind.Taiko.TaikoSpec;

namespace Nethermind.Taiko;

public class TaikoTransactionProcessor(
    ITransactionProcessor.IBlobBaseFeeCalculator blobBaseFeeCalculator,
    ISpecProvider specProvider,
    IWorldState worldState,
    IVirtualMachine virtualMachine,
    ICodeInfoRepository? codeInfoRepository,
    ILogManager? logManager
    ) : TransactionProcessorBase(blobBaseFeeCalculator, specProvider, worldState, virtualMachine, codeInfoRepository, logManager)
{
    protected override TransactionResult ValidateStatic(Transaction tx, BlockHeader header, IReleaseSpec spec, ExecutionOptions opts,
        in IntrinsicGas intrinsicGas)
        => base.ValidateStatic(tx, header, spec, tx.IsAnchorTx ? opts | ExecutionOptions.SkipValidationAndCommit : opts, in intrinsicGas);

    protected override TransactionResult BuyGas(Transaction tx, IReleaseSpec spec, ITxTracer tracer, ExecutionOptions opts,
                in UInt256 effectiveGasPrice, out UInt256 premiumPerGas, out UInt256 senderReservedGasPayment, out UInt256 blobBaseFee)
        => base.BuyGas(tx, spec, tracer, tx.IsAnchorTx ? opts | ExecutionOptions.SkipValidationAndCommit : opts, in effectiveGasPrice, out premiumPerGas, out senderReservedGasPayment, out blobBaseFee);

    protected override GasConsumed Refund(Transaction tx, BlockHeader header, IReleaseSpec spec, ExecutionOptions opts,
        in TransactionSubstate substate, in long unspentGas, in UInt256 gasPrice, int codeInsertRefunds, long floorGas)
        => base.Refund(tx, header, spec, tx.IsAnchorTx ? opts | ExecutionOptions.SkipValidationAndCommit : opts, substate, unspentGas, gasPrice, codeInsertRefunds, floorGas);

    protected override void PayFees(Transaction tx, BlockHeader header, IReleaseSpec spec, ITxTracer tracer,
        in TransactionSubstate substate, long spentGas, in UInt256 premiumPerGas, in UInt256 blobBaseFee, int statusCode)
    {
        UInt256 tipFees = (UInt256)spentGas * premiumPerGas;
        UInt256 baseFees = (UInt256)spentGas * header.BaseFeePerGas;

        // If the account has been destroyed during the execution, the balance is already set
        // as zero. So there is no need to create the account and pay the fees to the beneficiary,
        // except for the case when a restore is required due to a failure.
        bool gasBeneficiaryNotDestroyed = !substate.DestroyList.Contains(header.GasBeneficiary);
        if (statusCode == StatusCode.Failure || gasBeneficiaryNotDestroyed)
        {
            WorldState.AddToBalanceAndCreateIfNotExists(header.GasBeneficiary!, tipFees, spec);
        }

        if (!tx.IsAnchorTx && !baseFees.IsZero && spec.FeeCollector is not null)
        {
            if (((ITaikoReleaseSpec)spec).IsOntakeEnabled)
            {
                byte basefeeSharingPctg = header.DecodeOntakeExtraData() ?? 0;

                UInt256 feeCoinbase = baseFees * basefeeSharingPctg / 100;

                if (statusCode == StatusCode.Failure || gasBeneficiaryNotDestroyed)
                {
                    WorldState.AddToBalanceAndCreateIfNotExists(header.GasBeneficiary!, feeCoinbase, spec);
                }

                UInt256 feeTreasury = baseFees - feeCoinbase;
                WorldState.AddToBalanceAndCreateIfNotExists(spec.FeeCollector, feeTreasury, spec);
            }
            else
            {
                WorldState.AddToBalanceAndCreateIfNotExists(spec.FeeCollector, baseFees, spec);
            }
        }

        if (tracer.IsTracingFees)
            tracer.ReportFees(tipFees, baseFees);
    }

    protected override TransactionResult IncrementNonce(Transaction tx, BlockHeader header, IReleaseSpec spec, ITxTracer tracer, ExecutionOptions opts)
    {
        if (tx.IsAnchorTx)
            WorldState.CreateAccountIfNotExists(tx.SenderAddress!, UInt256.Zero, UInt256.Zero);

        return base.IncrementNonce(tx, header, spec, tracer, opts);
    }
}
