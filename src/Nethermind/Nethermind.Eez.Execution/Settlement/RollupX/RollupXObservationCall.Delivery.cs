// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using Nethermind.Blockchain.Tracing;
using Nethermind.Core;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;

namespace Nethermind.Eez.Execution.Settlement.RollupX;

internal sealed partial class RollupXObservationCall
{
    private ObservationOutcome ExecuteDelivery(RollupXObservationClaim claim, Address proxy, Address manager,
        bool retain, CancellationToken cancellation)
    {
        RollupXObservationDelivery delivery = claim.Delivery!;
        if (claim.Mode != ObservationMode.Lookup || claim.Callbacks.Length != 0)
            throw new EezSettlementException("Mutable observation has an inconsistent delivery program.");
        cancellation.ThrowIfCancellationRequested();
        BlockExecutionContext block = virtualMachine.BlockExecutionContext;
        TxExecutionContext transaction = virtualMachine.TxExecutionContext;
        if (block.Spec.IsEip8037Enabled) throw new EezSettlementException("Observation replay does not support state-gas accounting.");
        Snapshot snapshot = worldState.TakeSnapshot();
        if (snapshot.StorageSnapshot.TransientStorageSnapshot != Snapshot.EmptyPosition)
            throw new EezSettlementException("Observation replay requires empty transient storage at a transaction boundary.");
        bool completed = false;
        try
        {
            Address system = rules?.SystemAddress ?? EezConstants.SystemAddress;
            if (!delivery.Value.IsZero) worldState.AddToBalanceAndCreateIfNotExists(system, delivery.Value, block.Spec, out _);
            ProbeProcessor processor = new(specs, worldState, virtualMachine, code, logs);
            Transaction call = new()
            {
                Type = EezConstants.NativeSystemTxType,
                SenderAddress = system,
                To = manager,
                Value = delivery.Value,
                Data = delivery.Data,
                GasLimit = EezChainRules.RollupXSystemTxGasCap,
                ChainId = specs.ChainId,
            };
            DeliveryTracer capture = new(claim, proxy);
            using CancellationTxTracer tracer = new(capture, cancellation);
            TransactionResult result = processor.Run(call, tracer);
            cancellation.ThrowIfCancellationRequested();
            if (!result.TransactionExecuted || capture.StatusCode != StatusCode.Success || capture.Target is null)
                throw new EezSettlementException("Observation delivery failed to validate its target execution.");
            if (rules?.FinalizesProtocolBalances == true)
                foreach (Address account in rules.ProtocolBalanceAccounts)
                {
                    UInt256 balance = worldState.GetBalance(account);
                    if (!balance.IsZero) worldState.SubtractFromBalance(account, balance, block.Spec);
                }
            completed = true;
            return capture.Target;
        }
        finally
        {
            try { if (!retain || !completed) worldState.Restore(snapshot); }
            finally
            {
                virtualMachine.SetBlockExecutionContext(in block);
                virtualMachine.SetTxExecutionContext(in transaction);
            }
        }
    }

    private sealed class DeliveryTracer(RollupXObservationClaim claim, Address proxy) : CallOutputTracer
    {
        private readonly Stack<bool> _frames = new();
        private bool _started;
        public override bool IsTracingActions => true;
        internal ObservationOutcome? Target { get; private set; }

        private bool Match(Address from, Address? to, in UInt256 value, ReadOnlyMemory<byte> input, ExecutionType type) =>
            !_started && type == ExecutionType.CALL && from == proxy && to == claim.Target &&
            value == claim.Value && input.Span.SequenceEqual(claim.Data);

        public override void ReportAction(ulong gas, UInt256 value, Address from, Address to,
            ReadOnlyMemory<byte> input, ExecutionType type, bool precompile = false)
        {
            bool root = Match(from, to, value, input, type);
            _started |= root;
            _frames.Push(root);
        }

        public override void ReportRejectedAction(ulong gas, ulong left, UInt256 value, Address from, Address? to,
            ReadOnlyMemory<byte> input, ExecutionType type, EvmExceptionType error, bool precompile = false)
        {
            if (!Match(from, to, value, input, type)) return;
            _started = true;
            Target = new(false, []);
        }

        public override void ReportActionEnd(ulong gas, ReadOnlyMemory<byte> output) => End(true, output);
        public override void ReportActionEnd(ulong gas, Address address, ReadOnlyMemory<byte> code) => End(true, code);
        public override void ReportActionRevert(ulong gas, ReadOnlyMemory<byte> output) => End(false, output);
        public override void ReportActionError(EvmExceptionType error) => End(false, ReadOnlyMemory<byte>.Empty);

        private void End(bool success, ReadOnlyMemory<byte> output)
        {
            if (!_frames.TryPop(out bool root)) throw new EezSettlementException("Observation delivery has an unmatched call frame.");
            if (root) Target = new(success, output.ToArray());
        }
    }
}
