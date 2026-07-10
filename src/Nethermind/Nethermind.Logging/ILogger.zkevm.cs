// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;

namespace Nethermind.Logging;

#pragma warning disable NETH003 // Build variant: only one of ILogger.std.cs / ILogger.zkevm.cs is compiled per build
/// <summary>zkEVM no-op <see cref="ILogger"/>: all flags literal <c>false</c>, all log methods empty + inlined.</summary>
public readonly struct ILogger(InterfaceLogger logger) : IEquatable<ILogger>
{
    /// <summary>Always <c>false</c>.</summary>
    public bool IsTrace { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => false; }
    /// <summary>Always <c>false</c>.</summary>
    public bool IsDebug { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => false; }
    /// <summary>Always <c>false</c>.</summary>
    public bool IsInfo { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => false; }
    /// <summary>Always <c>false</c>.</summary>
    public bool IsWarn { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => false; }
    /// <summary>Always <c>false</c>.</summary>
    public bool IsError { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => false; }

#if DEBUG
    /// <summary>No-op.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetDebugMode() { }
#endif

    /// <summary>Underlying logger; stored so callers may <c>lock</c> on it.</summary>
    public InterfaceLogger UnderlyingLogger
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => logger;
    }

    /// <summary>No-op.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Debug(string text) { }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Equals(ILogger other) => UnderlyingLogger == other.UnderlyingLogger;

    /// <summary>No-op.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Error(string text, Exception? ex = null) { }

    /// <summary>No-op.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Info(string text) { }

    /// <summary>No-op.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Trace(string text) { }

    /// <summary>No-op.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Warn(string text) { }

    /// <summary>No-op.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void DebugError(
        [InterpolatedStringHandlerArgument("")] ref DebugInterpolatedStringHandler handler,
        Exception? ex = null)
    { }

    /// <summary>No-op.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void DebugError(string text, Exception? ex = null) { }

    /// <summary>No-op.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void DebugWarn(
        [InterpolatedStringHandlerArgument("")] ref DebugInterpolatedStringHandler handler)
    { }

    /// <summary>No-op.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void DebugWarn(string text) { }

    /// <summary>No-op.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void TraceError(
        [InterpolatedStringHandlerArgument("")] ref TraceInterpolatedStringHandler handler,
        Exception? ex = null)
    { }

    /// <summary>No-op.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void TraceError(string text, Exception? ex = null) { }

    /// <summary>No-op.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void TraceWarn(
        [InterpolatedStringHandlerArgument("")] ref TraceInterpolatedStringHandler handler)
    { }

    /// <summary>No-op.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void TraceWarn(string text) { }
}
