// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using Nethermind.Evm.Tracing;

namespace Nethermind.Evm.TransactionProcessing;

#pragma warning disable NETH003 // Build variant: only one of TracerFlags.std.cs / TracerFlags.zkevm.cs is compiled per build
/// <summary>zkEVM <see cref="TracerFlags"/>: the flags the guest compiles away are literal <c>false</c> and never read.</summary>
/// <remarks><see cref="DispatchFlags.Validate"/> rejects a tracer that sets any of them on every EVM-executed transaction.</remarks>
internal readonly struct TracerFlags(ITxTracer tracer)
{
    public readonly bool IsTracingState = tracer.IsTracingState;
    public readonly bool IsTracingReceipt = tracer.IsTracingReceipt;
    public readonly bool IsCollectingLogs = tracer.IsCollectingLogs;
    public readonly bool IsTracingMemory = tracer.IsTracingMemory;
    public readonly bool IsTracingOutput = tracer is not ITxOutputTracingFilter { IsTracingOutput: false };
    public bool IsTracingLogs { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => DispatchFlags.ConstTracing; }
    public bool IsTracingInstructions { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => DispatchFlags.ConstTracing; }
    public bool IsTracingAccess { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => DispatchFlags.ConstTracing; }
    public bool IsTracingRefunds { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => DispatchFlags.ConstTracing; }
    public bool IsTracingActions { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => DispatchFlags.ConstTracing; }
}
