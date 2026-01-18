// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
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

namespace Nethermind.Optimism;

public class OptimismTransactionProcessor(
    ITransactionProcessor.IBlobBaseFeeCalculator blobBaseFeeCalculator,
    ISpecProvider specProvider,
    IWorldState worldState,
    IVirtualMachine virtualMachine,
    ILogManager logManager,
    ICostHelper costHelper,
    IOptimismSpecHelper opSpecHelper,
    ICodeInfoRepository? codeInfoRepository
    ) : EthereumTransactionProcessorBase(blobBaseFeeCalculator, specProvider, worldState, virtualMachine, codeInfoRepository, logManager)
{
    private UInt256? _currentTxL1Cost;

    protected override TransactionResult Execute<TLogTracing>(Transaction tx, ITxTracer tracer, ExecutionOptions opts)
    {
        if (tx.SupportsBlobs)
        {
            // No blob txs in optimism
            return TransactionResult.MalformedTransaction;
        }

        BlockHeader header = VirtualMachine.BlockExecutionContext.Header;
        IReleaseSpec spec = SpecProvider.GetSpec(header);
        _currentTxL1Cost = null;
        if (tx.IsDeposit())
        {
            WorldState.AddToBalanceAndCreateIfNotExists(tx.SenderAddress!, tx.Mint, spec);
        }

        Snapshot snapshot = WorldState.TakeSnapshot();

        TransactionResult result = base.Execute<TLogTracing>(tx, tracer, opts);

        if (!result && tx.IsDeposit() && result.Error != TransactionResult.ErrorType.BlockGasLimitExceeded)
        {
            // deposit tx should be included
            WorldState.Restore(snapshot);
            if (!WorldState.AccountExists(tx.SenderAddress!))
            {
                WorldState.CreateAccount(tx.SenderAddress!, 0, 1);
            }
            else
            {
                WorldState.IncrementNonce(tx.SenderAddress!);
            }
            header.GasUsed += tx.GasLimit;
            tracer.MarkAsFailed(tx.To!, tx.GasLimit, [], $"failed deposit: {result.ErrorDescription}");
            result = TransactionResult.Ok;
        }

        return result;
    }

    protected override TransactionResult BuyGas<TLogTracing>(Transaction tx, IReleaseSpec spec, ITxTracer tracer, ExecutionOptions opts,
        in UInt256 effectiveGasPrice, out UInt256 premiumPerGas, out UInt256 senderReservedGasPayment, out UInt256 blobBaseFee)
    {
        premiumPerGas = UInt256.Zero;
        senderReservedGasPayment = UInt256.Zero;
        blobBaseFee = UInt256.Zero;

        bool validate = ShouldValidateGas(tx, opts);

        UInt256 senderBalance = WorldState.GetBalance(tx.SenderAddress!);

        if (tx.IsDeposit() && !tx.IsOPSystemTransaction && senderBalance < tx.ValueRef)
        {
            return TransactionResult.InsufficientSenderBalance;
        }

        if (!tx.IsDeposit())
        {
            BlockHeader header = VirtualMachine.BlockExecutionContext.Header;
            if (validate && !tx.TryCalculatePremiumPerGas(header.BaseFeePerGas, out premiumPerGas))
            {
                if (TLogTracing.IsActive) Logger.Trace($"Invalid tx {tx.Hash} ({"MINER_PREMIUM_IS_NEGATIVE"})");
                return TransactionResult.MinerPremiumNegative;
            }

            if (UInt256.SubtractUnderflow(in senderBalance, in tx.ValueRef, out UInt256 balanceLeft))
            {
                if (TLogTracing.IsActive) Logger.Trace($"Invalid tx {tx.Hash} (INSUFFICIENT_SENDER_BALANCE: ({tx.SenderAddress})_BALANCE = {senderBalance})");
                return TransactionResult.InsufficientSenderBalance;
            }

            UInt256 l1Cost = _currentTxL1Cost ??= costHelper.ComputeL1Cost(tx, header, WorldState);
            UInt256 maxOperatorCost = costHelper.ComputeOperatorCost(tx.GasLimit, header, WorldState);

            if (UInt256.SubtractUnderflow(balanceLeft, l1Cost + maxOperatorCost, out balanceLeft))
            {
                if (TLogTracing.IsActive) Logger.Trace($"Invalid tx {tx.Hash} (INSUFFICIENT_SENDER_BALANCE: ({tx.SenderAddress})_BALANCE = {senderBalance})");
                return TransactionResult.InsufficientSenderBalance;
            }

            bool overflows = UInt256.MultiplyOverflow((UInt256)tx.GasLimit, tx.MaxFeePerGas, out UInt256 maxGasFee);
            if (spec.IsEip1559Enabled && !tx.IsFree() && (overflows || balanceLeft < maxGasFee))
            {
                if (TLogTracing.IsActive) Logger.Trace($"Invalid tx {tx.Hash} (INSUFFICIENT_MAX_FEE_PER_GAS_FOR_SENDER_BALANCE: ({tx.SenderAddress})_BALANCE = {senderBalance}, MAX_FEE_PER_GAS: {tx.MaxFeePerGas})");
                return TransactionResult.InsufficientMaxFeePerGasForSenderBalance;
            }

            overflows = UInt256.MultiplyOverflow((UInt256)tx.GasLimit, effectiveGasPrice, out senderReservedGasPayment);
            if (overflows || senderReservedGasPayment > balanceLeft)
            {
                if (TLogTracing.IsActive) Logger.Trace($"Invalid tx {tx.Hash} (INSUFFICIENT_SENDER_BALANCE: ({tx.SenderAddress})_BALANCE = {senderBalance})");
                return TransactionResult.InsufficientSenderBalance;
            }

            senderReservedGasPayment += l1Cost; // no overflow here, otherwise previous check would fail
        }

        if (!senderReservedGasPayment.IsZero)
            WorldState.SubtractFromBalance(tx.SenderAddress!, senderReservedGasPayment, spec);

        return TransactionResult.Ok;
    }

    protected override TransactionResult IncrementNonce<TLogTracing>(Transaction tx, BlockHeader header, IReleaseSpec spec, ITxTracer tracer, ExecutionOptions opts)
    {
        if (!tx.IsDeposit())
            return base.IncrementNonce<TLogTracing>(tx, header, spec, tracer, opts);

        WorldState.IncrementNonce(tx.SenderAddress!);
        return TransactionResult.Ok;
    }

    protected override TransactionResult ValidateSender<TLogTracing>(Transaction tx, BlockHeader header, IReleaseSpec spec, ITxTracer tracer, ExecutionOptions opts) =>
        tx.IsDeposit() ? TransactionResult.Ok : base.ValidateSender<TLogTracing>(tx, header, spec, tracer, opts);

    protected override void PayFees(Transaction tx, BlockHeader header, IReleaseSpec spec, ITxTracer tracer,
        in TransactionSubstate substate, long spentGas, in UInt256 premiumPerGas, in UInt256 blobGasFee, int statusCode)
    {
        if (!tx.IsDeposit())
        {
            // Skip coinbase payments for deposit tx in Regolith
            base.PayFees(tx, header, spec, tracer, substate, spentGas, premiumPerGas, blobGasFee, statusCode);

            if (opSpecHelper.IsBedrock(header))
            {
                UInt256 l1Cost = _currentTxL1Cost ??= costHelper.ComputeL1Cost(tx, header, WorldState);
                WorldState.AddToBalanceAndCreateIfNotExists(opSpecHelper.L1FeeReceiver!, l1Cost, spec);
            }

            if (opSpecHelper.IsIsthmus(header))
            {
                UInt256 operatorCostMax = costHelper.ComputeOperatorCost(tx.GasLimit, header, WorldState);
                UInt256 operatorCostUsed = costHelper.ComputeOperatorCost(spentGas, header, WorldState);

                if (operatorCostMax > operatorCostUsed)
                {
                    // Refund the rest
                    WorldState.AddToBalance(tx.SenderAddress!, operatorCostMax - operatorCostUsed, spec);
                }

                // Transfer to fee recipient
                WorldState.AddToBalance(PreDeploys.OperatorFeeRecipient, operatorCostUsed, spec);
            }
        }
    }

    protected override GasConsumed Refund<TLogTracing>(Transaction tx, BlockHeader header, IReleaseSpec spec, ExecutionOptions opts,
        in TransactionSubstate substate, in EthereumGasPolicy unspentGas, in UInt256 gasPrice, int codeInsertRefunds, EthereumGasPolicy floorGas)
    {
        // if deposit: skip refunds, skip tipping coinbase
        // Regolith changes this behaviour to report the actual gasUsed instead of always reporting all gas used.
        if (tx.IsDeposit() && !opSpecHelper.IsRegolith(header))
        {
            // Record deposits as using all their gas
            // System Transactions are special & are not recorded as using any gas (anywhere)
            var gas = tx.IsOPSystemTransaction ? 0 : tx.GasLimit;
            return gas;
        }

        return base.Refund<TLogTracing>(tx, header, spec, opts, substate, unspentGas, gasPrice, codeInsertRefunds, floorGas);
    }
}
