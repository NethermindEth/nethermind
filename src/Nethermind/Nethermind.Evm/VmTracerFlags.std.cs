// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Evm.Tracing;

namespace Nethermind.Evm;

#pragma warning disable NETH003 // Build variant: only one of VmTracerFlags.std.cs / VmTracerFlags.zkevm.cs is compiled per build
/// <summary>Per-transaction snapshot of the <see cref="ITxTracer"/> flags the virtual machine branches on.</summary>
/// <remarks>The zkEVM build reads only the flags its tracers may set. See <c>VmTracerFlags.zkevm.cs</c>.</remarks>
internal readonly struct VmTracerFlags
{
    public readonly bool IsTracingActions;
    public readonly bool IsTracingRefunds;
    public readonly bool IsTracingAccess;
    public readonly bool IsTracingOpLevelStorage;
    public readonly bool IsTracingInstructions;
    public readonly bool IsTracingLogs;
    public readonly bool IsCancelable;

    /// <summary>Whether nested call output may be staged in reusable scratch, which no tracer of it may hold on to.</summary>
    /// <remarks>Never set for action tracers: parity action results keep the VM output arrays, per <see cref="ITxTracer.ReportActionEnd(ulong, System.ReadOnlyMemory{byte})"/>.</remarks>
    public readonly bool AllowsReturnScratch;

    public VmTracerFlags(ITxTracer tracer)
    {
        IsTracingActions = tracer.IsTracingActions;
        IsTracingRefunds = tracer.IsTracingRefunds;
        IsTracingAccess = tracer.IsTracingAccess;
        IsTracingOpLevelStorage = tracer.IsTracingOpLevelStorage;
        IsTracingInstructions = tracer.IsTracingInstructions;
        IsTracingLogs = tracer.IsTracingLogs;
        IsCancelable = tracer.IsCancelable;
        AllowsReturnScratch = !IsTracingActions && !IsTracingInstructions && !tracer.IsTracingMemory && !tracer.IsTracingReturnData;
    }
}
