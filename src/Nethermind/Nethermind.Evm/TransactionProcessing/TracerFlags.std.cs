// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Evm.Tracing;

namespace Nethermind.Evm.TransactionProcessing;

#pragma warning disable NETH003 // Build variant: only one of TracerFlags.std.cs / TracerFlags.zkevm.cs is compiled per build
/// <summary>Per-transaction snapshot of the <see cref="ITxTracer"/> flags the processor branches on.</summary>
/// <remarks>A tracer's flags are fixed for the transaction it traces, yet each read is an interface call, two
/// through a forwarding tracer such as the block receipts tracer. The flags are copied, not the tracer,
/// so the snapshot does not keep the tracer alive while a pooled processor sits idle.
/// <see cref="ITxTracer.IsTracing"/> is left out: it combines most other flags and is read only on halt paths.
/// The zkEVM build reads only the flags its tracers may set. See <c>TracerFlags.zkevm.cs</c>.</remarks>
internal readonly struct TracerFlags(ITxTracer tracer)
{
    public readonly bool IsTracingState = tracer.IsTracingState;
    public readonly bool IsTracingReceipt = tracer.IsTracingReceipt;
    public readonly bool IsCollectingLogs = tracer.IsCollectingLogs;
    public readonly bool IsTracingLogs = tracer.IsTracingLogs;
    public readonly bool IsTracingInstructions = tracer.IsTracingInstructions;
    public readonly bool IsTracingMemory = tracer.IsTracingMemory;
    public readonly bool IsTracingAccess = tracer.IsTracingAccess;
    public readonly bool IsTracingRefunds = tracer.IsTracingRefunds;
    public readonly bool IsTracingActions = tracer.IsTracingActions;
    public readonly bool IsTracingOutput = tracer is not ITxOutputTracingFilter { IsTracingOutput: false };
}
