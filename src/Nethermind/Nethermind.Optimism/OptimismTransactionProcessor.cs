using System;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
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
        IOPConfigHelper opConfigHelper) : base(specProvider, worldState, virtualMachine, logManager)
    {
        _l1CostHelper = l1CostHelper;
        _opConfigHelper = opConfigHelper;
    }

    protected override void Execute(Transaction tx, BlockHeader header, ITxTracer tracer, ExecutionOptions opts)
    {
        if (tx.IsDeposit())
        {
            IReleaseSpec spec = SpecProvider.GetSpec(header);

            WorldState.AddToBalanceAndCreateIfNotExists(tx.SenderAddress!, tx.Mint, spec);

            if (opts.HasFlag(ExecutionOptions.Commit) || !spec.IsEip658Enabled)
                WorldState.Commit(spec);
        }

        base.Execute(tx, header, tracer, opts);
    }

    protected override bool ExecuteEVMCall(Transaction tx, BlockHeader header, IReleaseSpec spec, ITxTracer tracer, ExecutionOptions opts,
        in long gasAvailable, in ExecutionEnvironment env, out TransactionSubstate? substate, out long spentGas,
        out byte statusCode)
    {
        bool callResult = base.ExecuteEVMCall(tx, header, spec, tracer, opts, in gasAvailable, in env, out substate, out spentGas, out statusCode);

        if (tx.IsOPSystemTransaction && !_opConfigHelper.IsRegolith(header))
            spentGas = 0;

        return callResult;
    }

    protected override bool BuyGas(Transaction tx, BlockHeader header, IReleaseSpec spec, ITxTracer tracer, ExecutionOptions opts,
        in UInt256 effectiveGasPrice, out UInt256 premiumPerGas, out UInt256 senderReservedGasPayment)
    {
        premiumPerGas = UInt256.Zero;
        senderReservedGasPayment = UInt256.Zero;

        bool validate = !opts.HasFlag(ExecutionOptions.NoValidation);

        if (validate && !tx.IsDeposit())
        {
            if (!tx.TryCalculatePremiumPerGas(header.BaseFeePerGas, out premiumPerGas))
            {
                TraceLogInvalidTx(tx, "MINER_PREMIUM_IS_NEGATIVE");
                QuickFail(tx, header, spec, tracer, "miner premium is negative");
                return false;
            }

            UInt256 senderBalance = WorldState.GetBalance(tx.SenderAddress!);
            if (UInt256.SubtractUnderflow(senderBalance, tx.Value, out UInt256 balanceLeft))
            {
                TraceLogInvalidTx(tx, $"INSUFFICIENT_SENDER_BALANCE: ({tx.SenderAddress})_BALANCE = {senderBalance}");
                QuickFail(tx, header, spec, tracer, "insufficient sender balance");
                return false;
            }

            UInt256 l1Cost = _l1CostHelper.ComputeL1Cost(tx, WorldState, header.Number, header.Timestamp, tx.IsDeposit());
            if (UInt256.SubtractUnderflow(balanceLeft, l1Cost, out balanceLeft))
            {
                TraceLogInvalidTx(tx, $"INSUFFICIENT_SENDER_BALANCE: ({tx.SenderAddress})_BALANCE = {senderBalance}");
                QuickFail(tx, header, spec, tracer, "insufficient sender balance");
                return false;
            }

            bool overflows = UInt256.MultiplyOverflow((UInt256)tx.GasLimit, tx.MaxFeePerGas, out UInt256 maxGasFee);
            if (spec.IsEip1559Enabled && !tx.IsFree() && (overflows || balanceLeft < maxGasFee))
            {
                TraceLogInvalidTx(tx, $"INSUFFICIENT_MAX_FEE_PER_GAS_FOR_SENDER_BALANCE: ({tx.SenderAddress})_BALANCE = {senderBalance}, MAX_FEE_PER_GAS: {tx.MaxFeePerGas}");
                QuickFail(tx, header, spec, tracer, "insufficient MaxFeePerGas for sender balance");
                return false;
            }

            overflows = UInt256.MultiplyOverflow((UInt256)tx.GasLimit, effectiveGasPrice, out senderReservedGasPayment);
            if (overflows || senderReservedGasPayment > balanceLeft)
            {
                TraceLogInvalidTx(tx, $"INSUFFICIENT_SENDER_BALANCE: ({tx.SenderAddress})_BALANCE = {senderBalance}");
                QuickFail(tx, header, spec, tracer, "insufficient sender balance");
                return false;
            }

            senderReservedGasPayment += l1Cost; // no overflow here, otherwise previous check would fail
        }

        if (validate)
            WorldState.SubtractFromBalance(tx.SenderAddress!, senderReservedGasPayment, spec);

        return true;
    }

    protected override bool IncrementNonce(Transaction tx, BlockHeader header, IReleaseSpec spec, ITxTracer tracer, ExecutionOptions opts)
    {
        return tx.IsDeposit() || base.IncrementNonce(tx, header, spec, tracer, opts);
    }

    protected override bool ValidateSender(Transaction tx, BlockHeader header, IReleaseSpec spec, ITxTracer tracer, ExecutionOptions opts)
    {
        return tx.IsDeposit() || base.ValidateSender(tx, header, spec, tracer, opts);
    }

    protected override bool PayFees(Transaction tx, BlockHeader header, IReleaseSpec spec, ITxTracer tracer,
        in TransactionSubstate substate, in long spentGas, in UInt256 premiumPerGas, in byte statusCode)
    {
        if (tx.IsDeposit())
        {
            // Skip coinbase payments for deposit tx in Regolith
            return true;
        }

        if (!base.PayFees(tx, header, spec, tracer, substate, spentGas, premiumPerGas, statusCode))
            // this should never happen, but just to be consistent
            return false;

        if (_opConfigHelper.IsBedrock(header))
        {
            UInt256 l1Cost = _l1CostHelper.ComputeL1Cost(tx, WorldState, header.Number, header.Timestamp, tx.IsDeposit());
            WorldState.AddToBalanceAndCreateIfNotExists(_opConfigHelper.L1FeeReceiver, l1Cost, spec);
        }

        return true;
    }

    protected override long Refund(Transaction tx, BlockHeader header, IReleaseSpec spec, ExecutionOptions opts,
        in TransactionSubstate substate, in long unspentGas, in UInt256 gasPrice)
    {
        // if deposit: skip refunds, skip tipping coinbase
        if (tx.IsDeposit())
        {
            // Regolith changes this behaviour to report the actual gasUsed instead of always reporting all gas used.
            if (!_opConfigHelper.IsRegolith(header))
            {
                // Record deposits as using all their gas
                // System Transactions are special & are not recorded as using any gas (anywhere)
                return tx.IsOPSystemTransaction ? 0 : tx.GasLimit;
            }

            return tx.GasLimit - (!substate.IsError ? unspentGas : 0);
        }

        return base.Refund(tx, header, spec, opts, substate, unspentGas, gasPrice);
    }
}

public class OptimismTransactionProcessorFactory : ITransactionProcessorFactory
{
    private readonly IL1CostHelper _l1CostHelper;
    private readonly IOPConfigHelper _opConfigHelper;

    public OptimismTransactionProcessorFactory(IL1CostHelper l1CostHelper, IOPConfigHelper opConfigHelper)
    {
        _l1CostHelper = l1CostHelper;
        _opConfigHelper = opConfigHelper;
    }

    public ITransactionProcessor Create(ISpecProvider? specProvider, IWorldState? worldState, IVirtualMachine? virtualMachine, ILogManager? logManager)
        => new OptimismTransactionProcessor(specProvider, worldState, virtualMachine, logManager, _l1CostHelper, _opConfigHelper);
}
