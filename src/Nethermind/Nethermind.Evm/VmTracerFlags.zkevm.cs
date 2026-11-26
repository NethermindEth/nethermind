// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using Nethermind.Evm.Tracing;

namespace Nethermind.Evm;

#pragma warning disable NETH003 // Build variant: only one of VmTracerFlags.std.cs / VmTracerFlags.zkevm.cs is compiled per build
/// <summary>zkEVM <see cref="VmTracerFlags"/>: the flags the guest compiles away are literal <c>false</c> and never read.</summary>
/// <remarks><see cref="DispatchFlags.Validate"/> rejects a tracer that sets any of them.</remarks>
internal readonly struct VmTracerFlags(ITxTracer tracer)
{
    public bool IsTracingActions { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => DispatchFlags.ConstTracing; }
    public bool IsTracingRefunds { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => DispatchFlags.ConstTracing; }
    public bool IsTracingAccess { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => DispatchFlags.ConstTracing; }
    public bool IsTracingOpLevelStorage { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => DispatchFlags.ConstTracing; }
    public bool IsTracingInstructions { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => DispatchFlags.ConstTracing; }
    public bool IsTracingLogs { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => DispatchFlags.ConstTracing; }
    public bool IsCancelable { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => DispatchFlags.ConstCancelable; }

    /// <inheritdoc cref="VmTracerFlags.AllowsReturnScratch"/>
    public readonly bool AllowsReturnScratch = !tracer.IsTracingMemory && !tracer.IsTracingReturnData;

    /// <inheritdoc cref="VmTracerFlags.AllowsTopLevelReturnScratch"/>
    public readonly bool AllowsTopLevelReturnScratch =
        !tracer.IsTracingMemory && !tracer.IsTracingReturnData && tracer is ITxOutputTracingFilter { IsTracingOutput: false };
}
