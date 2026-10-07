// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;

namespace Nethermind.Logging;

/// <summary>
/// Struct to wrap InterfaceLogger in that when created sets values in struct for
/// IsTrace, IsDebug, IsInfo, IsWarn, IsError so the guards are a fast check inline against
/// the struct rather than being an interface call each time.
/// </summary>
#if !DEBUG
readonly
#endif
public struct ILogger : IEquatable<ILogger>
{
    private readonly InterfaceLogger _logger;
#if !DEBUG
    readonly
#endif
    private LogLevel _value;

    public ILogger(InterfaceLogger logger)
    {
        _logger = logger;
        if (logger.IsTrace) _value |= LogLevel.Trace;
        if (logger.IsDebug) _value |= LogLevel.Debug;
        if (logger.IsInfo) _value |= LogLevel.Info;
        if (logger.IsWarn) _value |= LogLevel.Warn;
        if (logger.IsError) _value |= LogLevel.Error;
    }

    public readonly bool IsTrace => (_value & LogLevel.Trace) != 0;
    public readonly bool IsDebug => (_value & LogLevel.Debug) != 0;
    public readonly bool IsInfo => (_value & LogLevel.Info) != 0;
    public readonly bool IsWarn => (_value & LogLevel.Warn) != 0;
    public readonly bool IsError => (_value & LogLevel.Error) != 0;

#if DEBUG
    public void SetDebugMode() => _value |= LogLevel.Debug;
#endif

    public InterfaceLogger UnderlyingLogger => _logger;

    // We need to use NoInlining as the call sites (should) be already performing the guard checks,
    // otherwise they will be executing code to build error strings to pass etc, so we don't want to
    // inline the code for a second check.
    [MethodImpl(MethodImplOptions.NoInlining)]
    public readonly void Debug(string text)
    {
        if (IsDebug) _logger.Debug(text);
    }

    public bool Equals(ILogger other) => _logger == other._logger;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public readonly void Error(string text, Exception? ex = null)
    {
        if (IsError) _logger.Error(text, ex);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public readonly void Info(string text)
    {
        if (IsInfo) _logger.Info(text);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public readonly void Trace(string text)
    {
        if (IsTrace) _logger.Trace(text);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public readonly void Warn(string text)
    {
        if (IsWarn) _logger.Warn(text);
    }

    /// <summary>
    /// Logs at <see cref="InterfaceLogger.Error"/> severity, but only when <see cref="IsDebug"/> is true.
    /// Replaces the manual <c>if (logger.IsDebug) logger.Error($"DEBUG/ERROR: ...")</c> idiom.
    /// Interpolation of the message is skipped entirely when <see cref="IsDebug"/> is false,
    /// so the callsite pays no allocation cost in the common (disabled) case.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public readonly void DebugError(
        [InterpolatedStringHandlerArgument("")] ref DebugInterpolatedStringHandler handler,
        Exception? ex = null)
    {
        if (IsDebug) _logger.Error("DEBUG/ERROR: " + handler.ToStringAndClear(), LogEventKind.DebugError, ex);
    }

    /// <summary>
    /// Plain-string overload of <see cref="DebugError(ref DebugInterpolatedStringHandler, Exception)"/>
    /// for callers that pass a literal message with no interpolation.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public readonly void DebugError(string text, Exception? ex = null)
    {
        if (IsDebug) _logger.Error("DEBUG/ERROR: " + text, LogEventKind.DebugError, ex);
    }

    /// <summary>
    /// Logs at <see cref="InterfaceLogger.Warn"/> severity, but only when <see cref="IsDebug"/> is true.
    /// Replaces the manual <c>if (logger.IsDebug) logger.Warn($"DEBUG/WARN: ...")</c> idiom.
    /// Interpolation of the message is skipped entirely when <see cref="IsDebug"/> is false,
    /// so the callsite pays no allocation cost in the common (disabled) case.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public readonly void DebugWarn(
        [InterpolatedStringHandlerArgument("")] ref DebugInterpolatedStringHandler handler)
    {
        if (IsDebug) _logger.Warn("DEBUG/WARN: " + handler.ToStringAndClear(), LogEventKind.DebugWarn);
    }

    /// <summary>
    /// Plain-string overload of <see cref="DebugWarn(ref DebugInterpolatedStringHandler)"/>
    /// for callers that pass a literal message with no interpolation.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public readonly void DebugWarn(string text)
    {
        if (IsDebug) _logger.Warn("DEBUG/WARN: " + text, LogEventKind.DebugWarn);
    }

    /// <summary>
    /// Logs at <see cref="InterfaceLogger.Error"/> severity, but only when <see cref="IsTrace"/> is true.
    /// Replaces the manual <c>if (logger.IsTrace) logger.Error($"TRACE/ERROR: ...")</c> idiom.
    /// Interpolation of the message is skipped entirely when <see cref="IsTrace"/> is false,
    /// so the callsite pays no allocation cost in the common (disabled) case.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public readonly void TraceError(
        [InterpolatedStringHandlerArgument("")] ref TraceInterpolatedStringHandler handler,
        Exception? ex = null)
    {
        if (IsTrace) _logger.Error("TRACE/ERROR: " + handler.ToStringAndClear(), LogEventKind.TraceError, ex);
    }

    /// <summary>
    /// Plain-string overload of <see cref="TraceError(ref TraceInterpolatedStringHandler, Exception)"/>
    /// for callers that pass a literal message with no interpolation.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public readonly void TraceError(string text, Exception? ex = null)
    {
        if (IsTrace) _logger.Error("TRACE/ERROR: " + text, LogEventKind.TraceError, ex);
    }

    /// <summary>
    /// Logs at <see cref="InterfaceLogger.Warn"/> severity, but only when <see cref="IsTrace"/> is true.
    /// Replaces the manual <c>if (logger.IsTrace) logger.Warn($"TRACE/WARN: ...")</c> idiom.
    /// Interpolation of the message is skipped entirely when <see cref="IsTrace"/> is false,
    /// so the callsite pays no allocation cost in the common (disabled) case.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public readonly void TraceWarn(
        [InterpolatedStringHandlerArgument("")] ref TraceInterpolatedStringHandler handler)
    {
        if (IsTrace) _logger.Warn("TRACE/WARN: " + handler.ToStringAndClear(), LogEventKind.TraceWarn);
    }

    /// <summary>
    /// Plain-string overload of <see cref="TraceWarn(ref TraceInterpolatedStringHandler)"/>
    /// for callers that pass a literal message with no interpolation.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public readonly void TraceWarn(string text)
    {
        if (IsTrace) _logger.Warn("TRACE/WARN: " + text, LogEventKind.TraceWarn);
    }

    [Flags]
    private enum LogLevel
    {
        Trace = 1,
        Debug = 2,
        Info = 4,
        Warn = 8,
        Error = 16
    }
}
