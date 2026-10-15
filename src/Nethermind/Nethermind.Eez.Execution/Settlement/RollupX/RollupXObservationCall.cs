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
            claim.Value, claim.Mode, cancellationToken);
        if (outcome.Success != claim.Expected.Success || !outcome.Output.AsSpan().SequenceEqual(claim.Expected.Output))
            throw new EezSettlementException("Observation result differs from its FIFO claim.");
    }

    public ObservationOutcome ExecuteAtTransactionBoundary(Address caller, Address target, ReadOnlyMemory<byte> data,
        in UInt256 value, ObservationMode mode, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!value.IsZero) throw new EezSettlementException("Value-bearing observations are not supported.");
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
            IVirtualMachine machine = mode == ObservationMode.Lookup ? virtualMachine : new StaticMachine(virtualMachine);
            ProbeProcessor processor = new(specs, worldState, machine, code, logs);
            Transaction transaction = new()
            {
                SenderAddress = caller,
                To = target,
                Data = data,
                GasLimit = mode == ObservationMode.Proxy ? ProxyGasLimit : TargetGasLimit,
                ChainId = specs.ChainId,
            };
            ObservationTracer output = new();
            using CancellationTxTracer tracer = new(output, cancellationToken);
            TransactionResult result = processor.Run(transaction, tracer);
            cancellationToken.ThrowIfCancellationRequested();
            if (!result.TransactionExecuted)
                throw new EezSettlementException($"Observation transaction is invalid: {result}");
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

    private sealed class ObservationTracer : CallOutputTracer
    {
        public override bool IsTracingActions => true;

        public override void ReportAction(ulong gas, UInt256 value, Address from, Address to,
            ReadOnlyMemory<byte> input, ExecutionType callType, bool isPrecompileCall = false) =>
            RejectDispatch(callType);

        public override void ReportRejectedAction(ulong gas, ulong gasLeft, UInt256 value, Address from, Address? to,
            ReadOnlyMemory<byte> input, ExecutionType callType, EvmExceptionType error, bool isPrecompileCall = false) =>
            RejectDispatch(callType);

        private static void RejectDispatch(ExecutionType callType)
        {
            if (callType is ExecutionType.CALL or ExecutionType.STATICCALL)
                throw new EezSettlementException("Observation replay does not support nested dispatch calls.");
        }
    }

    private sealed class StaticMachine(IVirtualMachine inner) : IVirtualMachine
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
            using VmState<EthereumGasPolicy> staticState = VmState<EthereumGasPolicy>.RentTopLevel(
                state.Gas, state.ExecutionType, state.Env, in state.AccessTracker, in state.Snapshot, isStatic: true);
            TransactionSubstate result = inner.ExecuteTransaction<TTracingInst>(staticState, stateProvider, tracer);
            state.Gas = staticState.Gas;
            return result;
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
