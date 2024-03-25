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

namespace Nethermind.Optimism;

public class OptimismTransactionProcessor : TransactionProcessor
{
    private readonly IL1CostHelper _l1CostHelper;
    private readonly IOPConfigHelper _opConfigHelper;

    public OptimismTransactionProcessor(
        ISpecProvider? specProvider,
        IWorldState? worldState,
        IVirtualMachine? virtualMachine,
        ILogManager? logManager,
        IL1CostHelper l1CostHelper,
        IOPConfigHelper opConfigHelper,
        ICodeInfoRepository? codeInfoRepository
        ) : base(specProvider, worldState, virtualMachine, codeInfoRepository, logManager)
    {
        _l1CostHelper = l1CostHelper;
        _opConfigHelper = opConfigHelper;
    }

    private UInt256? _currentTxL1Cost;

    protected override TransactionResult Execute(Transaction tx, in BlockExecutionContext blCtx, ITxTracer tracer, ExecutionOptions opts)
    {
        IReleaseSpec spec = SpecProvider.GetSpec(blCtx.Header);
        _currentTxL1Cost = null;
        if (tx.IsDeposit())
        {
            WorldState.AddToBalanceAndCreateIfNotExists(tx.SenderAddress!, tx.Mint, spec);

            if (opts.HasFlag(ExecutionOptions.Commit) || !spec.IsEip658Enabled)
                WorldState.Commit(spec);
        }

        return base.Execute(tx, in blCtx, tracer, opts);
    }

    protected override TransactionResult ValidateStatic(Transaction tx, BlockHeader header, IReleaseSpec spec, ITxTracer tracer, ExecutionOptions opts,
        out long intrinsicGas)
    {
        TransactionResult result = base.ValidateStatic(tx, header, spec, tracer, opts, out intrinsicGas);
        if (tx.IsDeposit() && !tx.IsOPSystemTransaction && !result)
        {
            if (!WorldState.AccountExists(tx.SenderAddress!))
            {
                WorldState.CreateAccount(tx.SenderAddress!, 0, 1);
            }
            else
            {
                WorldState.IncrementNonce(tx.SenderAddress!);
            }
        }

        return result;
    }

    protected override TransactionResult BuyGas(Transaction tx, BlockHeader header, IReleaseSpec spec, ITxTracer tracer, ExecutionOptions opts,
        in UInt256 effectiveGasPrice, out UInt256 premiumPerGas, out UInt256 senderReservedGasPayment)
    {
        premiumPerGas = UInt256.Zero;
        senderReservedGasPayment = UInt256.Zero;

        bool validate = !opts.HasFlag(ExecutionOptions.NoValidation);

        UInt256 senderBalance = WorldState.GetBalance(tx.SenderAddress!);

        if (tx.IsDeposit() && !tx.IsOPSystemTransaction && senderBalance < tx.Value)
        {
            if (!WorldState.AccountExists(tx.SenderAddress!))
            {
                WorldState.CreateAccount(tx.SenderAddress!, 0, 1);
            }
            else
            {
                WorldState.IncrementNonce(tx.SenderAddress!);
            }
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

            UInt256 l1Cost = _currentTxL1Cost ??= _l1CostHelper.ComputeL1Cost(tx, header, WorldState);
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
            WorldState.SubtractFromBalance(tx.SenderAddress!, senderReservedGasPayment, spec);

        return TransactionResult.Ok;
    }

    protected override TransactionResult IncrementNonce(Transaction tx, BlockHeader header, IReleaseSpec spec, ITxTracer tracer, ExecutionOptions opts)
    {
        if (!tx.IsDeposit())
            return base.IncrementNonce(tx, header, spec, tracer, opts);

        WorldState.IncrementNonce(tx.SenderAddress!);
        return TransactionResult.Ok;
    }

    protected override TransactionResult ValidateSender(Transaction tx, BlockHeader header, IReleaseSpec spec, ITxTracer tracer, ExecutionOptions opts) =>
        tx.IsDeposit() ? TransactionResult.Ok : base.ValidateSender(tx, header, spec, tracer, opts);

    protected override void PayFees(Transaction tx, BlockHeader header, IReleaseSpec spec, ITxTracer tracer,
        in TransactionSubstate substate, in long spentGas, in UInt256 premiumPerGas, in byte statusCode)
    {
        if (!tx.IsDeposit())
        {
            // Skip coinbase payments for deposit tx in Regolith
            base.PayFees(tx, header, spec, tracer, substate, spentGas, premiumPerGas, statusCode);

            if (_opConfigHelper.IsBedrock(header))
            {
                UInt256 l1Cost = _currentTxL1Cost ??= _l1CostHelper.ComputeL1Cost(tx, header, WorldState);
                WorldState.AddToBalanceAndCreateIfNotExists(_opConfigHelper.L1FeeReceiver, l1Cost, spec);
            }
        }
    }

    protected override long Refund(Transaction tx, BlockHeader header, IReleaseSpec spec, ExecutionOptions opts,
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

        return base.Refund(tx, header, spec, opts, substate, unspentGas, gasPrice);
    }
}
