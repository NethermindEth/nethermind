// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;

namespace Nethermind.Logging;

#pragma warning disable NETH003 // Build variant: only one of ILogger.std.cs / ILogger.zkevm.cs is compiled per build
/// <summary>
/// zkEVM-build specialization of <see cref="ILogger"/>: every level flag is the literal constant
/// <c>false</c> and every log method is an empty body marked <see cref="MethodImplOptions.AggressiveInlining"/>.
/// </summary>
/// <remarks>
/// Inside a zk prover the host has no logging surface, and any work performed to build a log
/// message is wasted - including allocations behind interpolated strings. Because the level
/// predicates here are literal <c>false</c>, the C# compiler emits the interpolated-string
/// handler's <c>out bool shouldAppend</c> as a constant <c>false</c> at every call site, so the
/// JIT eliminates the entire <c>Append*</c> chain. The empty, inlined method bodies then
/// collapse the trailing call itself, leaving zero IL behind a log statement.
/// </remarks>
public readonly struct ILogger : IEquatable<ILogger>
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ILogger(InterfaceLogger logger) => _ = logger;

    public bool IsTrace { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => false; }
    public bool IsDebug { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => false; }
    public bool IsInfo { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => false; }
    public bool IsWarn { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => false; }
    public bool IsError { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => false; }

#if DEBUG
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetDebugMode() { }
#endif

    public InterfaceLogger UnderlyingLogger
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => null!;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Debug(string text) { }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Equals(ILogger other) => true;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Error(string text, Exception? ex = null) { }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Info(string text) { }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Trace(string text) { }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Warn(string text) { }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void DebugError(
        [InterpolatedStringHandlerArgument("")] ref DebugInterpolatedStringHandler handler,
        Exception? ex = null)
    { }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void DebugError(string text, Exception? ex = null) { }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void DebugWarn(
        [InterpolatedStringHandlerArgument("")] ref DebugInterpolatedStringHandler handler)
    { }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void DebugWarn(string text) { }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void TraceError(
        [InterpolatedStringHandlerArgument("")] ref TraceInterpolatedStringHandler handler,
        Exception? ex = null)
    { }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void TraceError(string text, Exception? ex = null) { }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void TraceWarn(
        [InterpolatedStringHandlerArgument("")] ref TraceInterpolatedStringHandler handler)
    { }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void TraceWarn(string text) { }
}
