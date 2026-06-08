// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.GasPolicy;
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
    ILogManager? logManager,
    IFeeRecorder? feeRecorder = null
    ) : EthereumTransactionProcessorBase(blobBaseFeeCalculator, specProvider, worldState, virtualMachine, codeInfoRepository, logManager, feeRecorder)
{
    protected override TransactionResult ValidateStatic(Transaction tx, BlockHeader header, IReleaseSpec spec, ExecutionOptions opts,
        in IntrinsicGas<EthereumGasPolicy> intrinsicGas)
        => base.ValidateStatic(tx, header, spec, tx.IsAnchorTx ? opts | ExecutionOptions.SkipValidationAndCommit : opts, in intrinsicGas);

    protected override TransactionResult BuyGas(Transaction tx, IReleaseSpec spec, ITxTracer tracer, ExecutionOptions opts,
        in UInt256 effectiveGasPrice, out UInt256 premiumPerGas, out UInt256 senderReservedGasPayment,
        out UInt256 blobBaseFee)
    {
        if (tx.IsAnchorTx)
        {
            premiumPerGas = UInt256.Zero;
            senderReservedGasPayment = UInt256.Zero;
            blobBaseFee = UInt256.Zero;
            return TransactionResult.Ok;
        }

        return base.BuyGas(tx, spec, tracer, opts, in effectiveGasPrice, out premiumPerGas, out senderReservedGasPayment, out blobBaseFee);
    }

    protected override void PayFees(Transaction tx, BlockHeader header, IReleaseSpec spec, ITxTracer tracer,
        in TransactionSubstate substate, long spentGas, in UInt256 premiumPerGas, in UInt256 blobBaseFee, int statusCode)
    {
        UInt256 tipFees = (UInt256)spentGas * premiumPerGas;
        UInt256 baseFees = (UInt256)spentGas * header.BaseFeePerGas;

        // If the account has been destroyed during the execution, the balance is already set
        // as zero. So there is no need to create the account and pay the fees to the beneficiary,
        // except for the case when a restore is required due to a failure.
        bool gasBeneficiaryNotDestroyed = !substate.DestroyListContains(header.GasBeneficiary);
        bool payBeneficiary = statusCode == StatusCode.Failure || gasBeneficiaryNotDestroyed;

        CreditGasBeneficiary(header.GasBeneficiary!, tipFees, spec, payBeneficiary);

        if (!tx.IsAnchorTx && !baseFees.IsZero && spec.FeeCollector is not null)
        {
            ITaikoReleaseSpec taikoSpec = (ITaikoReleaseSpec)spec;
            if (taikoSpec.IsOntakeEnabled || taikoSpec.IsShastaEnabled)
            {
                byte basefeeSharingPct = (taikoSpec.IsShastaEnabled ? header.DecodeShastaBasefeeSharingPctg() : header.DecodeOntakeExtraData()) ?? 0;

                UInt256 feeCoinbase = baseFees * basefeeSharingPct / 100;
                CreditGasBeneficiary(header.GasBeneficiary!, feeCoinbase, spec, payBeneficiary);

                UInt256 feeTreasury = baseFees - feeCoinbase;
                CreditFeeCollector(spec.FeeCollector, feeTreasury, spec);
            }
            else
            {
                CreditFeeCollector(spec.FeeCollector, baseFees, spec);
            }
        }

        if (tracer.IsTracingFees)
            tracer.ReportFees(tipFees, baseFees);
    }

    // The recorder is called with zero amount when payBeneficiary is false so the
    // dependency edge for higher txs is still recorded.
    private void CreditGasBeneficiary(Address beneficiary, in UInt256 amount, IReleaseSpec spec, bool payBeneficiary)
    {
        if (FeeRecorder is not null)
        {
            UInt256 payable = payBeneficiary ? amount : UInt256.Zero;
            FeeRecorder.RecordFee(beneficiary, in payable, payBeneficiary);
        }
        else if (payBeneficiary)
        {
            WorldState.AddToBalanceAndCreateIfNotExists(beneficiary, amount, spec);
        }
    }

    private void CreditFeeCollector(Address feeCollector, in UInt256 amount, IReleaseSpec spec)
    {
        if (FeeRecorder is not null)
        {
            FeeRecorder.RecordFee(feeCollector, in amount, createAccount: !amount.IsZero);
        }
        else
        {
            WorldState.AddToBalanceAndCreateIfNotExists(feeCollector, amount, spec);
        }
    }

    protected override TransactionResult IncrementNonce(Transaction tx, BlockHeader header, IReleaseSpec spec, ITxTracer tracer, ExecutionOptions opts)
    {
        if (tx.IsAnchorTx)
            WorldState.CreateAccountIfNotExists(tx.SenderAddress!, UInt256.Zero, UInt256.Zero);

        return base.IncrementNonce(tx, header, spec, tracer, opts);
    }

    protected override void PayRefund(Transaction tx, UInt256 refundAmount, IReleaseSpec spec)
    {
        if (!tx.IsAnchorTx)
        {
            base.PayRefund(tx, refundAmount, spec);
        }
    }
}
