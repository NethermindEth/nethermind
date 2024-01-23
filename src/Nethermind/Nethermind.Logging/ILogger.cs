// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;

namespace Nethermind.Logging;

/// <summary>
/// Struct to wrap InterfaceLogger in that that when created sets values in struct for
/// IsTrace, IsDebug, IsInfo, IsWarn, IsError so the guards are a fast check inline against
/// the struct rather than being an interface call each time.
/// </summary>
#if DEBUG
public struct ILogger
#else
public readonly struct ILogger
#endif
{
    private readonly InterfaceLogger _logger;
#if DEBUG
    private LogLevel _value;
#else
    private readonly LogLevel _value;
#endif

    public ILogger(InterfaceLogger logger)
    {
        _logger = logger;
        if (logger.IsTrace) _value |= LogLevel.Trace;
        if (logger.IsDebug) _value |= LogLevel.Debug;
        if (logger.IsInfo) _value |= LogLevel.Info;
        if (logger.IsWarn) _value |= LogLevel.Warn;
        if (logger.IsError) _value |= LogLevel.Error;
    }

    public bool IsTrace => (_value & LogLevel.Trace) != 0;
    public bool IsDebug => (_value & LogLevel.Debug) != 0;
    public bool IsInfo => (_value & LogLevel.Info) != 0;
    public bool IsWarn => (_value & LogLevel.Warn) != 0;
    public bool IsError => (_value & LogLevel.Error) != 0;

#if DEBUG
    public void SetDebugMode() => _value |= LogLevel.Debug;
#endif

    public InterfaceLogger UnderlyingLogger => _logger;

    // We need to use NoInlining as the call sites (should) be already performing the guard checks,
    // otherwise they will be executing code to build error strings to pass etc, so we don't want to
    // inline the code for a second check.
    [MethodImpl(MethodImplOptions.NoInlining)]
    public void Debug(string text)
    {
        if (IsDebug)
            _logger.Debug(text);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public void Error(string text, Exception ex = null)
    {
        if (IsError)
            _logger.Error(text, ex);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public void Info(string text)
    {
        if (IsInfo)
            _logger.Info(text);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public void Trace(string text)
    {
        if (IsTrace)
            _logger.Trace(text);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public void Warn(string text)
    {
        if (IsWarn)
            _logger.Warn(text);
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
