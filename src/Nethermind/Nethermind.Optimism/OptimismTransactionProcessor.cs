// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State;

namespace Nethermind.Optimism;

public class OptimismTransactionProcessor : TransactionProcessor
{
    private readonly IL1CostHelper _l1CostHelper;
    private readonly IOPConfigHelper _opConfigHelper;

    public OptimismTransactionProcessor(
        ISpecProvider? specProvider,
        IVirtualMachine? virtualMachine,
        ILogManager? logManager,
        IL1CostHelper l1CostHelper,
        IOPConfigHelper opConfigHelper) : base(specProvider, virtualMachine, logManager)
    {
        _l1CostHelper = l1CostHelper;
        _opConfigHelper = opConfigHelper;
    }

    private UInt256? _currentTxL1Cost;

    protected override TransactionResult Execute(Transaction tx, IWorldState worldState, in BlockExecutionContext blCtx, ITxTracer tracer, ExecutionOptions opts)
    {
        if (tx.SupportsBlobs)
        {
            // No blob txs in optimism
            return TransactionResult.MalformedTransaction;
        }

        IReleaseSpec spec = SpecProvider.GetSpec(blCtx.Header);
        _currentTxL1Cost = null;
        if (tx.IsDeposit())
        {
            worldState.AddToBalanceAndCreateIfNotExists(tx.SenderAddress!, tx.Mint, spec);
        }

        Snapshot snapshot = worldState.TakeSnapshot();

        TransactionResult result = base.Execute(tx, worldState, blCtx, tracer, opts);

        if (!result && tx.IsDeposit() && result.Error != "block gas limit exceeded")
        {
            // deposit tx should be included
            worldState.Restore(snapshot);
            if (!worldState.AccountExists(tx.SenderAddress!))
            {
                worldState.CreateAccount(tx.SenderAddress!, 0, 1);
            }
            else
            {
                worldState.IncrementNonce(tx.SenderAddress!);
            }
            blCtx.Header.GasUsed += tx.GasLimit;
            tracer.MarkAsFailed(tx.To!, tx.GasLimit, Array.Empty<byte>(), $"failed deposit: {result.Error}");
            result = TransactionResult.Ok;
        }

        return result;
    }

    protected override TransactionResult ValidateStatic(Transaction tx, BlockHeader header, IReleaseSpec spec, ITxTracer tracer, ExecutionOptions opts,
        out long intrinsicGas)
    {
        TransactionResult result = base.ValidateStatic(tx, header, spec, tracer, opts, out intrinsicGas);

        return result;
    }

    protected override TransactionResult BuyGas(Transaction tx, IWorldState worldState, BlockHeader header, IReleaseSpec spec, ITxTracer tracer, ExecutionOptions opts,
        in UInt256 effectiveGasPrice, out UInt256 premiumPerGas, out UInt256 senderReservedGasPayment)
    {
        premiumPerGas = UInt256.Zero;
        senderReservedGasPayment = UInt256.Zero;

        bool validate = !opts.HasFlag(ExecutionOptions.NoValidation);

        UInt256 senderBalance = worldState.GetBalance(tx.SenderAddress!);

        if (tx.IsDeposit() && !tx.IsOPSystemTransaction && senderBalance < tx.Value)
        {
            return "insufficient sender balance";
        }

        if (validate && !tx.IsDeposit())
        {
            if (!tx.TryCalculatePremiumPerGas(header.BaseFeePerGas, out premiumPerGas))
            {
                TraceLogInvalidTx(tx, "MINER_PREMIUM_IS_NEGATIVE");
                return "miner premium is negative";
            }

            if (UInt256.SubtractUnderflow(senderBalance, tx.Value, out UInt256 balanceLeft))
            {
                TraceLogInvalidTx(tx, $"INSUFFICIENT_SENDER_BALANCE: ({tx.SenderAddress})_BALANCE = {senderBalance}");
                return "insufficient sender balance";
            }

            UInt256 l1Cost = _currentTxL1Cost ??= _l1CostHelper.ComputeL1Cost(tx, header, worldState);
            if (UInt256.SubtractUnderflow(balanceLeft, l1Cost, out balanceLeft))
            {
                TraceLogInvalidTx(tx, $"INSUFFICIENT_SENDER_BALANCE: ({tx.SenderAddress})_BALANCE = {senderBalance}");
                return "insufficient sender balance";
            }

            bool overflows = UInt256.MultiplyOverflow((UInt256)tx.GasLimit, tx.MaxFeePerGas, out UInt256 maxGasFee);
            if (spec.IsEip1559Enabled && !tx.IsFree() && (overflows || balanceLeft < maxGasFee))
            {
                TraceLogInvalidTx(tx, $"INSUFFICIENT_MAX_FEE_PER_GAS_FOR_SENDER_BALANCE: ({tx.SenderAddress})_BALANCE = {senderBalance}, MAX_FEE_PER_GAS: {tx.MaxFeePerGas}");
                return "insufficient MaxFeePerGas for sender balance";
            }

            overflows = UInt256.MultiplyOverflow((UInt256)tx.GasLimit, effectiveGasPrice, out senderReservedGasPayment);
            if (overflows || senderReservedGasPayment > balanceLeft)
            {
                TraceLogInvalidTx(tx, $"INSUFFICIENT_SENDER_BALANCE: ({tx.SenderAddress})_BALANCE = {senderBalance}");
                return "insufficient sender balance";
            }

            senderReservedGasPayment += l1Cost; // no overflow here, otherwise previous check would fail
        }

        if (validate)
            worldState.SubtractFromBalance(tx.SenderAddress!, senderReservedGasPayment, spec);

        return TransactionResult.Ok;
    }

    protected override TransactionResult IncrementNonce(Transaction tx, IWorldState worldState, BlockHeader header, IReleaseSpec spec, ITxTracer tracer, ExecutionOptions opts)
    {
        if (!tx.IsDeposit())
            return base.IncrementNonce(tx, worldState, header, spec, tracer, opts);

        worldState.IncrementNonce(tx.SenderAddress!);
        return TransactionResult.Ok;
    }

    protected override TransactionResult ValidateSender(Transaction tx, IWorldState worldState, BlockHeader header, IReleaseSpec spec, ITxTracer tracer, ExecutionOptions opts) =>
        tx.IsDeposit() ? TransactionResult.Ok : base.ValidateSender(tx, worldState, header, spec, tracer, opts);

    protected override void PayFees(Transaction tx, IWorldState worldState, BlockHeader header, IReleaseSpec spec, ITxTracer tracer,
        in TransactionSubstate substate, in long spentGas, in UInt256 premiumPerGas, in byte statusCode)
    {
        if (!tx.IsDeposit())
        {
            // Skip coinbase payments for deposit tx in Regolith
            base.PayFees(tx, worldState, header, spec, tracer, substate, spentGas, premiumPerGas, statusCode);

            if (_opConfigHelper.IsBedrock(header))
            {
                UInt256 l1Cost = _currentTxL1Cost ??= _l1CostHelper.ComputeL1Cost(tx, header, worldState);
                worldState.AddToBalanceAndCreateIfNotExists(_opConfigHelper.L1FeeReceiver, l1Cost, spec);
            }
        }
    }

    protected override long Refund(Transaction tx, IWorldState worldState, BlockHeader header, IReleaseSpec spec, ExecutionOptions opts,
        in TransactionSubstate substate, in long unspentGas, in UInt256 gasPrice)
    {
        // if deposit: skip refunds, skip tipping coinbase
        // Regolith changes this behaviour to report the actual gasUsed instead of always reporting all gas used.
        if (tx.IsDeposit() && !_opConfigHelper.IsRegolith(header))
        {
            // Record deposits as using all their gas
            // System Transactions are special & are not recorded as using any gas (anywhere)
            return tx.IsOPSystemTransaction ? 0 : tx.GasLimit;
        }

        return base.Refund(tx, worldState, header, spec, opts, substate, unspentGas, gasPrice);
    }
}
