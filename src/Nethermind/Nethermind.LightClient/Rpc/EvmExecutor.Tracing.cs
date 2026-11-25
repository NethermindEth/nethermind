// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using Nethermind.Blockchain.Tracing.GethStyle;
using Nethermind.Blockchain.Tracing.GethStyle.Custom.Native.Call;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Eip2930;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Facade;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Int256;
using Nethermind.JsonRpc.Data;
using Nethermind.State;

namespace Nethermind.LightClient.Rpc;

internal sealed partial class EvmExecutor
{
    private readonly EthereumEcdsa _accessListEcdsa = new(specs.ChainId);

    internal Task<AccessListResultForRpc> CreateAccessListAsync(VerifiedHead head, TransactionForRpc request,
        Dictionary<Address, AccountOverride>? stateOverrides, bool optimize, CancellationToken token) =>
        ExecuteSingleAsync<AccessListResultForRpc>(head, request, SingleCallKind.AccessList, stateOverrides, null,
            (ITransactionProcessor processor, IWorldState state, Transaction tx, TransactionForRpc _,
                in BlockExecutionContext context, CancellationToken cancellation) =>
                    CreateAccessList(processor, state, tx, context, optimize, cancellation), TimeSpan.FromSeconds(60), token);

    private AccessListResultForRpc CreateAccessList(ITransactionProcessor processor, IWorldState state, Transaction tx,
        BlockExecutionContext context, bool optimize, CancellationToken token)
    {
        AccessList? original = tx.AccessList;
        try
        {
            (CallOutput output, _) = AccessListExecutor.Create(state, context.Header, tx, context.Spec, _accessListEcdsa,
                specs.ChainId, optimize, Run, token, maxIterations: 32);
            if (output.InputError) throw new RpcException(-32000, output.Error ?? "Access list execution failed.");
            return new(AccessListForRpc.FromAccessList(output.AccessList ?? original), output.GasSpent, output.Error);
        }
        finally { tx.AccessList = original; }

        TransactionResult Run(ITxTracer tracer)
        {
            if (tx.GetTipAboveFeeCapError(context.Spec) is { } error)
                return TransactionResult.ErrorType.MalformedTransaction.WithDetail(error);
            try { return processor.CallAndRestore(tx, in context, tracer); }
            catch (InsufficientBalanceException) { return TransactionResult.InsufficientSenderBalance; }
        }
    }

    internal Task<GethLikeTxTrace> TraceCallAsync(VerifiedHead head, TransactionForRpc request, GethTraceOptions? options,
        CancellationToken token)
    {
        TimeSpan timeout = TraceTimeLimit(options);
        return ExecuteSingleAsync<GethLikeTxTrace>(head, request, SingleCallKind.TraceCall, options!.StateOverrides, options.BlockOverrides,
            (ITransactionProcessor processor, IWorldState _, Transaction tx, TransactionForRpc _,
                in BlockExecutionContext context, CancellationToken cancellation) => RunTrace(processor, tx, in context, options, cancellation),
            timeout, token);
    }

    internal static TimeSpan TraceTimeLimit(GethTraceOptions? options)
    {
        if (options?.Tracer is not NativeCallTracer.CallTracer)
            throw new RpcException(-32602, "Light client tracing requires callTracer.");
        if (options.TxIndex is not null || options.TxHash is not null || options.StreamMode is true)
            throw new RpcException(-32602, "Transaction-position and streaming trace options are unavailable.");
        TimeSpan max = TimeSpan.FromSeconds(60);
        TimeSpan timeout;
        try { timeout = options.ResolveTimeout() ?? TimeSpan.FromSeconds(5); }
        catch (FormatException exception) { throw new RpcException(-32602, exception.Message); }
        if (timeout <= TimeSpan.Zero) throw new RpcException(-32602, "Trace timeout must be positive.");
        try { NativeCallTracerConfig.FromJson(options.TracerConfig, Nethermind.Serialization.Json.EthereumJsonSerializer.JsonOptions); }
        catch (JsonException) { throw new RpcException(-32602, "Invalid callTracer configuration."); }
        return timeout < max ? timeout : max;
    }

    private static GethLikeTxTrace RunTrace(ITransactionProcessor processor, Transaction tx, in BlockExecutionContext context,
        GethTraceOptions options, CancellationToken token)
    {
        NativeCallTracer tracer;
        try { tracer = new(tx, context.Spec, options); }
        catch (JsonException) { throw new RpcException(-32602, "Invalid callTracer configuration."); }
        using (tracer)
        {
            CallTraceBudget budget = new(tracer.IsTracingLogs);
            TransactionResult result = processor.CallAndRestore(tx, in context, new CompositeTxTracer(budget, tracer).WithCancellation(token));
            if (!result.TransactionExecuted) throw new RpcException(-32000, result.GetErrorMessage(null) ?? "Trace execution failed.");
            return tracer.BuildResult();
        }
    }

    /// <remarks>Charges buffered bytes and object overhead before the native tracer can retain them.</remarks>
    private sealed class CallTraceBudget(bool withLog) : TxTracer
    {
        private long _remaining = 1024 * 1024;
        private int _depth;
        public override bool IsTracingActions => true;
        public override bool IsTracingLogs => withLog;
        public override bool IsTracingReceipt => true;
        public override bool IsCollectingLogs => false;

        private void Charge(int bytes, int overhead = 0)
        {
            _remaining -= 2L * bytes + overhead;
            if (_remaining < 0) throw new RpcException(-32005, "Call trace exceeds the light-client result budget.");
        }

        public override void ReportAction(ulong gas, UInt256 value, Address from, Address to, ReadOnlyMemory<byte> input,
            ExecutionType callType, bool isPrecompileCall = false)
        {
            if (++_depth > 128) throw new RpcException(-32005, "Call trace exceeds the light-client depth limit.");
            Charge(input.Length, 512);
        }

        public override void ReportRejectedAction(ulong gas, ulong gasLeft, UInt256 value, Address from, Address to,
            ReadOnlyMemory<byte> input, ExecutionType callType, EvmExceptionType error, bool isPrecompileCall = false) => Charge(input.Length, 512);

        public override void ReportActionEnd(ulong gas, ReadOnlyMemory<byte> output) { Charge(output.Length); --_depth; }
        public override void ReportActionEnd(ulong gas, Address deploymentAddress, ReadOnlyMemory<byte> deployedCode) => ReportActionEnd(gas, deployedCode);
        public override void ReportActionRevert(ulong gas, ReadOnlyMemory<byte> output) => ReportActionEnd(gas, output);
        public override void ReportActionError(EvmExceptionType evmExceptionType) => --_depth;
        public override void ReportSelfDestruct(Address address, UInt256 balance, Address refundAddress) => Charge(0, 512);
        public override void ReportLog(LogEntry log) => Charge(log.Data.Length, 128 + 66 * log.Topics.Length);
        public override void MarkAsSuccess(Address recipient, in GasConsumed gasSpent, byte[] output, LogEntry[] logs,
            Hash256? stateRoot = null) => Charge(output.Length);
        public override void MarkAsFailed(Address recipient, in GasConsumed gasSpent, byte[] output, string? error,
            Hash256? stateRoot = null) => Charge(output?.Length ?? 0);
    }
}
