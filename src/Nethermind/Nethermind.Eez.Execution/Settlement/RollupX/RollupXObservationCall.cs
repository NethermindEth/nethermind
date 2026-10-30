// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Threading;
using Nethermind.Blockchain.Tracing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.GasPolicy;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;

namespace Nethermind.Eez.Execution.Settlement.RollupX;

internal sealed class RollupXObservationCall(
    ISpecProvider specs, IWorldState worldState, IVirtualMachine virtualMachine,
    ICodeInfoRepository code, ILogManager logs)
{
    internal const ulong TargetGasLimit = 30_000_000;
    internal const ulong ProxyGasLimit = 1_000_000;

    private static readonly Hash256 ProxySelector = Keccak.Compute("computeCrossChainProxyAddress(address,uint64)");

    public void VerifyClaim(RollupXObservationClaim claim, Address manager, CancellationToken cancellationToken)
    {
        if (claim.Mode is not (ObservationMode.Lookup or ObservationMode.Static))
            throw new EezSettlementException("Unknown FIFO observation mode.");
        byte[] proxyData = new byte[4 + 64];
        ProxySelector.Bytes[..4].CopyTo(proxyData);
        AbiWord.Write(proxyData.AsSpan(4, 32), claim.SourceAddress);
        BinaryPrimitives.WriteUInt64BigEndian(proxyData.AsSpan(60, 8), claim.SourceRollupId);
        ObservationOutcome proxy = ExecuteAtTransactionBoundary(Address.Zero, manager, proxyData,
            UInt256.Zero, ObservationMode.Proxy, cancellationToken);
        if (!proxy.Success || proxy.Output.Length != 32 || proxy.Output.AsSpan(0, 12).IndexOfAnyExcept((byte)0) >= 0)
            throw new EezSettlementException("Observed proxy address is not a canonical ABI address.");
        Address caller = new(proxy.Output.AsSpan(12));
        ObservationOutcome outcome = ExecuteAtTransactionBoundary(caller, claim.Target, claim.Data,
            claim.Value, claim.Mode, cancellationToken, new RollupXObservationDispatch(code, worldState, manager, claim));
        if (outcome.Success != claim.Expected.Success || !outcome.Output.AsSpan().SequenceEqual(claim.Expected.Output))
            throw new EezSettlementException("Observation result differs from its FIFO claim.");
    }

    public ObservationOutcome ExecuteAtTransactionBoundary(Address caller, Address target, ReadOnlyMemory<byte> data,
        in UInt256 value, ObservationMode mode, CancellationToken cancellationToken = default, RollupXObservationDispatch? dispatch = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!value.IsZero && mode != ObservationMode.Lookup)
            throw new EezSettlementException("Static observations cannot transfer value.");
        if (mode is not (ObservationMode.Lookup or ObservationMode.Static or ObservationMode.Proxy))
            throw new EezSettlementException("Unknown observation call mode.");
        if (virtualMachine.BlockExecutionContext.Spec.IsEip8037Enabled)
            throw new EezSettlementException("Observation replay does not support state-gas accounting.");

        BlockExecutionContext blockContext = virtualMachine.BlockExecutionContext;
        TxExecutionContext transactionContext = virtualMachine.TxExecutionContext;
        Snapshot snapshot = worldState.TakeSnapshot();
        if (snapshot.StorageSnapshot.TransientStorageSnapshot != Snapshot.EmptyPosition)
            throw new EezSettlementException("Observation replay requires a transaction boundary with empty transient storage.");
        try
        {
            IVirtualMachine machine = new ObservationMachine(virtualMachine, mode != ObservationMode.Lookup, dispatch);
            UInt256 balance = worldState.GetBalance(caller);
            if (balance < value)
                worldState.AddToBalanceAndCreateIfNotExists(caller, value - balance, blockContext.Spec, out _);
            ProbeProcessor processor = new(specs, worldState, machine, dispatch ?? code, logs);
            Transaction transaction = new()
            {
                SenderAddress = caller,
                To = target,
                Data = data,
                Value = value,
                GasLimit = mode == ObservationMode.Proxy ? ProxyGasLimit : TargetGasLimit,
                ChainId = specs.ChainId,
            };
            ObservationTracer output = new(dispatch);
            using CancellationTxTracer tracer = new(output, cancellationToken);
            TransactionResult result = processor.Run(transaction, tracer);
            cancellationToken.ThrowIfCancellationRequested();
            if (!result.TransactionExecuted)
                throw new EezSettlementException($"Observation transaction is invalid: {result}");
            dispatch?.EnsureConsumed();
            return new ObservationOutcome(output.StatusCode == StatusCode.Success, output.ReturnValue ?? []);
        }
        finally
        {
            try
            {
                worldState.Restore(snapshot);
            }
            finally
            {
                virtualMachine.SetBlockExecutionContext(in blockContext);
                virtualMachine.SetTxExecutionContext(in transactionContext);
            }
        }
    }

    private sealed class ProbeProcessor(ISpecProvider specs, IWorldState state, IVirtualMachine machine,
        ICodeInfoRepository code, ILogManager logs)
        : EthereumTransactionProcessorBase(BlobBaseFeeCalculator.Instance, specs, state, machine, code, logs)
    {
        public TransactionResult Run(Transaction transaction, ITxTracer tracer) =>
            Execute(transaction, tracer, ExecutionOptions.SkipValidation);

        protected override TransactionResult BuyGas(Transaction tx, IReleaseSpec spec, ITxTracer tracer, ExecutionOptions opts,
            in UInt256 effectiveGasPrice, out UInt256 premiumPerGas, out UInt256 senderReservedGasPayment, out UInt256 blobBaseFee)
        {
            premiumPerGas = senderReservedGasPayment = blobBaseFee = UInt256.Zero;
            return TransactionResult.Ok;
        }

        protected override void PayFees(Transaction tx, BlockHeader header, IReleaseSpec spec, ITxTracer tracer,
            in TransactionSubstate substate, ulong spentGas, in UInt256 premiumPerGas, in UInt256 effectiveGasPrice,
            in UInt256 blobBaseFee, int statusCode)
        { }
    }

    private sealed class ObservationTracer(RollupXObservationDispatch? dispatch) : CallOutputTracer, IInstructionTracingFilter
    {
        public override bool IsTracingActions => true;
        public override bool IsTracingInstructions => dispatch is not null;
        public UInt256 InstructionMask => DispatchMask;

        private static readonly UInt256 DispatchMask =
            (UInt256.One << (int)Instruction.CALL) | (UInt256.One << (int)Instruction.STATICCALL) |
            (UInt256.One << (int)Instruction.DELEGATECALL) | (UInt256.One << (int)Instruction.CALLCODE) |
            (UInt256.One << (int)Instruction.EXTCODESIZE) | (UInt256.One << (int)Instruction.EXTCODECOPY) |
            (UInt256.One << (int)Instruction.EXTCODEHASH) | (UInt256.One << (int)Instruction.SETCODEFROM);

        public override void StartOperation(int pc, Instruction opcode, ulong gas, in ExecutionEnvironment env) =>
            dispatch!.StartOperation(opcode);

        public override void ReportAction(ulong gas, UInt256 value, Address from, Address to,
            ReadOnlyMemory<byte> input, ExecutionType callType, bool isPrecompileCall = false) =>
            Report(from, to, value, input, callType, false);

        public override void ReportRejectedAction(ulong gas, ulong gasLeft, UInt256 value, Address from, Address? to,
            ReadOnlyMemory<byte> input, ExecutionType callType, EvmExceptionType error, bool isPrecompileCall = false) =>
            Report(from, to, value, input, callType, true);

        private void Report(Address from, Address? to, UInt256 value, ReadOnlyMemory<byte> input, ExecutionType type, bool rejected) =>
            dispatch?.Report(from, to, value, input, type, rejected);

    }

    private sealed class ObservationMachine(IVirtualMachine inner, bool forceStatic, RollupXObservationDispatch? dispatch) : IVirtualMachine
    {
        public ref readonly BlockExecutionContext BlockExecutionContext => ref inner.BlockExecutionContext;
        public ref readonly TxExecutionContext TxExecutionContext => ref inner.TxExecutionContext;
        public int OpCodeCount => inner.OpCodeCount;

        public void SetBlockExecutionContext(in BlockExecutionContext context) => inner.SetBlockExecutionContext(in context);
        public void SetTxExecutionContext(in TxExecutionContext context) => inner.SetTxExecutionContext(in context);
        public void FlushMetricsCounters() => inner.FlushMetricsCounters();

        public TransactionSubstate ExecuteTransaction<TTracingInst>(VmState<EthereumGasPolicy> state,
            IWorldState stateProvider, ITxTracer tracer) where TTracingInst : struct, IFlag
        {
            dispatch?.Begin(in state.AccessTracker);
            try
            {
                if (!forceStatic) return inner.ExecuteTransaction<TTracingInst>(state, stateProvider, tracer);
                using VmState<EthereumGasPolicy> staticState = VmState<EthereumGasPolicy>.RentTopLevel(
                    state.Gas, state.ExecutionType, state.Env, in state.AccessTracker, in state.Snapshot, isStatic: true);
                TransactionSubstate result = inner.ExecuteTransaction<TTracingInst>(staticState, stateProvider, tracer);
                state.Gas = staticState.Gas;
                return result;
            }
            finally { dispatch?.End(); }
        }
    }
}

internal enum ObservationMode
{
    Lookup,
    Static,
    Proxy,
}

internal sealed record ObservationOutcome(bool Success, byte[] Output);
