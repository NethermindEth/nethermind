// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Specs;
using Nethermind.Eez.Execution.Settlement.RollupX;
using Nethermind.Evm;
using Nethermind.Evm.GasPolicy;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;

namespace Nethermind.Eez.Execution;

/// <summary>
/// Executes EEZ system transactions under the chain's <see cref="EezChainRules"/>: zero fee, the chain's gas, and
/// <c>value</c> minted to the system address before the call.
/// </summary>
/// <remarks>
/// The mint happens in <see cref="PayValue"/>, which the base processor runs after it snapshots state for the
/// call, so a reverted or halted call discards the mint while gas use remains. Legacy envelopes advance
/// the system nonce; native-v1 envelopes never change it.
/// </remarks>
public sealed class EezTransactionProcessor(
    EezChainRules rules,
    ITransactionProcessor.IBlobBaseFeeCalculator blobBaseFeeCalculator,
    ISpecProvider? specProvider,
    IWorldState? worldState,
    IVirtualMachine? virtualMachine,
    ICodeInfoRepository? codeInfoRepository,
    ILogManager? logManager,
    bool parallel = false)
    : EthereumTransactionProcessorBase(blobBaseFeeCalculator, specProvider, worldState, virtualMachine, codeInfoRepository, logManager, parallel)
{
    private readonly NativeHistorySchedule? _historySchedule = rules.UsesNativeContract
        ? new(rules.Schedule!.GenesisTimestamp, rules.Schedule.BlockIntervalSeconds, rules.Schedule.SyncIntervalBlocks, rules.NativeHistoryOffset)
        : null;
    private BlockHeader? _groupHeader;
    private NativeExecutionGroup _group;
    private IReleaseSpec? _feeSpecSource;
    private IReleaseSpec? _withoutFeeCollector;

    /// <remarks>
    /// Legacy envelopes carry no gas and receive the position's budget here. Native-v1 envelopes encode
    /// that budget; their exact value is checked without changing the received transaction.
    /// </remarks>
    protected override TransactionResult Execute(Transaction tx, ITxTracer tracer, ExecutionOptions opts)
    {
        if (tx.Type == EezConstants.SystemTxType && !rules.UsesNativeSystemTransactions &&
            rules.SystemTransactionGas(VirtualMachine.BlockExecutionContext.Header) is { } gas)
        {
            tx.GasLimit = gas;
        }

        BlockHeader header = VirtualMachine.BlockExecutionContext.Header;
        NativeExecutionGroup next = default;
        if (rules.UsesNativeContract)
            next = CurrentGroup(header).After(tx, out _);
        Transaction original = tx;
        NativeHistoryResultTracer? historyResult = null;
        if (tx is NativeHistoryTransaction history)
        {
            if (!rules.UsesNativeContract || !tx.HasSystemTransactionFields(rules) || history.IsExecutionView)
                return TransactionResult.ErrorType.MalformedTransaction.WithDetail("Invalid dedicated history transaction fields");
            tx = history.ForExecution(_historySchedule!.SourceCutoff(header.Timestamp));
            if (rules.NativeFormat.UsesProxyHistory() && !opts.HasFlag(ExecutionOptions.Warmup))
            {
                historyResult = new NativeHistoryResultTracer();
                tracer = new CompositeTxTracer(tracer, historyResult);
            }
        }
        TransactionResult result = base.Execute(tx, tracer, opts);
        if (result && historyResult is { Valid: false })
            throw new InvalidBlockException(header, "Native history import must succeed without output or logs.");
        if (!ReferenceEquals(original, tx) && !opts.HasFlag(ExecutionOptions.Warmup))
        {
            original.SpentGas = tx.SpentGas;
            original.BlockGasUsed = tx.BlockGasUsed;
        }
        if (result && rules.UsesNativeContract && !opts.HasFlag(ExecutionOptions.Restore) && !opts.HasFlag(ExecutionOptions.Warmup))
        {
            _groupHeader = header;
            _group = next;
        }
        return result;
    }

    private NativeExecutionGroup CurrentGroup(BlockHeader header) => ReferenceEquals(_groupHeader, header) ? _group : default;

    protected override TransactionResult ValidateStatic(Transaction tx, BlockHeader header, IReleaseSpec spec, ExecutionOptions opts,
        in IntrinsicGas<EthereumGasPolicy> intrinsicGas) =>
        tx.IsEezSystemTransaction() && (!tx.HasSystemTransactionFields(rules) ||
            rules.UsesNativeSystemTransactions && (tx.ChainId != SpecProvider.ChainId || !rules.HasNativePositionGas(tx, header)))
            ? TransactionResult.ErrorType.MalformedTransaction.WithDetail("EEZ system transaction with non-protocol sender, target, gas or fee fields")
            : base.ValidateStatic(tx, header, spec, opts, in intrinsicGas);

    protected override SystemTransactionProcessor<EthereumGasPolicy> CreateSystemTransactionProcessor() =>
        new EezSystemTransactionProcessor(this, _blobBaseFeeCalculator, SpecProvider, WorldState, VirtualMachine, _codeInfoRepository, _logManager);

    protected override TransactionResult IncrementNonce(Transaction tx, BlockHeader header, IReleaseSpec spec, ITxTracer tracer, ExecutionOptions opts) =>
        (tx.Type == EezConstants.NativeSystemTxType || tx is NativeHistoryTransaction)
            ? TransactionResult.Ok
            : base.IncrementNonce(tx, header, spec, tracer, opts);

    protected override void DecrementNonce(Transaction tx)
    {
        if (tx.Type != EezConstants.NativeSystemTxType && tx is not NativeHistoryTransaction) base.DecrementNonce(tx);
    }

    protected override TransactionResult BuyGas(Transaction tx, IReleaseSpec spec, ITxTracer tracer, ExecutionOptions opts,
        in UInt256 effectiveGasPrice, out UInt256 premiumPerGas, out UInt256 senderReservedGasPayment, out UInt256 blobBaseFee)
    {
        if (!tx.IsEezSystemTransaction())
        {
            return base.BuyGas(tx, spec, tracer, opts, in effectiveGasPrice, out premiumPerGas, out senderReservedGasPayment, out blobBaseFee);
        }

        premiumPerGas = UInt256.Zero;
        senderReservedGasPayment = UInt256.Zero;
        blobBaseFee = UInt256.Zero;
        return UInt256.AddOverflow(WorldState.GetBalance(tx.SenderAddress!), tx.ValueRef, out _)
            ? TransactionResult.ErrorType.MalformedTransaction.WithDetail("EEZ system transaction mint overflows the system balance")
            : TransactionResult.Ok;
    }

    protected override void PayFees(Transaction tx, BlockHeader header, IReleaseSpec spec, ITxTracer tracer,
        in TransactionSubstate substate, ulong spentGas, in UInt256 premiumPerGas, in UInt256 effectiveGasPrice,
        in UInt256 blobBaseFee, int statusCode)
    {
        if (rules.UsesNativeSystemTransactions && spec.FeeCollector is { } nativeCollector)
        {
            ValidateNativeFeeCredit(tx, header, spec, in substate, spentGas, in premiumPerGas, in effectiveGasPrice, in blobBaseFee, statusCode, nativeCollector);
        }
        if (!rules.UsesNativeSystemTransactions && statusCode == StatusCode.Success && spec.FeeCollector is { } collector &&
            !spec.IsEip8037Enabled && substate.DestroyListContains(collector))
        {
            if (!ReferenceEquals(_feeSpecSource, spec))
            {
                _feeSpecSource = spec;
                _withoutFeeCollector = new WithoutFeeCollector(spec);
            }
            spec = _withoutFeeCollector!;
        }
        base.PayFees(tx, header, spec, tracer, in substate, spentGas, in premiumPerGas, in effectiveGasPrice, in blobBaseFee, statusCode);
        if (rules.UsesNativeContract)
        {
            CurrentGroup(header).After(tx, out bool completes);
            if (completes)
                foreach (Address address in rules.ProtocolBalanceAccounts)
                {
                    UInt256 balance = WorldState.GetBalance(address);
                    if (!balance.IsZero) WorldState.SubtractFromBalance(address, in balance, spec);
                }
        }
    }

    private void ValidateNativeFeeCredit(Transaction tx, BlockHeader header, IReleaseSpec spec, in TransactionSubstate substate,
        ulong spentGas, in UInt256 premiumPerGas, in UInt256 effectiveGasPrice, in UInt256 blobBaseFee, int statusCode, Address collector)
    {
        UInt256 credit = UInt256.Zero;
        if (!tx.IsFree() && spec.IsEip1559Enabled &&
            UInt256.MultiplyOverflow((UInt256)spentGas, UInt256.Min(header.BaseFeePerGas, effectiveGasPrice), out credit))
            throw new InvalidBlockException(header, "Native collector fee overflows uint256.");
        if (tx.SupportsBlobs && spec.IsEip4844FeeCollectorEnabled && UInt256.AddOverflow(credit, blobBaseFee, out credit))
            throw new InvalidBlockException(header, "Native collector fee overflows uint256.");
        if (credit.IsZero) return;
        UInt256 balance = WorldState.GetBalance(collector);
        if (collector == header.GasBeneficiary && (statusCode == StatusCode.Failure || !substate.DestroyListContains(collector) || spec.IsEip8037Enabled) &&
            (UInt256.MultiplyOverflow((UInt256)spentGas, premiumPerGas, out UInt256 tip) || UInt256.AddOverflow(balance, tip, out balance)))
            throw new InvalidBlockException(header, "Native collector balance overflows uint256.");
        if (UInt256.AddOverflow(balance, credit, out _)) throw new InvalidBlockException(header, "Native collector balance overflows uint256.");
    }

    protected override void PayValue(Transaction tx, IReleaseSpec spec, ExecutionOptions opts)
    {
        if (tx.IsEezSystemTransaction() && !tx.ValueRef.IsZero)
        {
            WorldState.AddToBalanceAndCreateIfNotExists(tx.SenderAddress!, in tx.ValueRef, spec);
        }

        base.PayValue(tx, spec, opts);
    }

    private sealed class WithoutFeeCollector(IReleaseSpec spec) : ReleaseSpecDecorator(spec)
    {
        public override Address? FeeCollector => null;
    }

    private sealed class EezSystemTransactionProcessor(
        EezTransactionProcessor owner,
        ITransactionProcessor.IBlobBaseFeeCalculator blobBaseFeeCalculator,
        ISpecProvider? specProvider,
        IWorldState? worldState,
        IVirtualMachine<EthereumGasPolicy>? virtualMachine,
        ICodeInfoRepository? codeInfoRepository,
        ILogManager? logManager)
        : SystemTransactionProcessor<EthereumGasPolicy>(blobBaseFeeCalculator, specProvider, worldState, virtualMachine, codeInfoRepository, logManager)
    {
        // Native-v1 shares Ethereum's system sender, but remains a metered transaction with normal call rollback.
        protected override TransactionResult Execute(Transaction tx, ITxTracer tracer, ExecutionOptions opts) =>
            tx.IsEezSystemTransaction() ? owner.Execute(tx, tracer, opts) : base.Execute(tx, tracer, opts);
    }
}
