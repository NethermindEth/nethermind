// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;

namespace Nethermind.Logging;

/// <summary>
/// Formats informational log messages, masking fields marked with <c>:hide</c> when enabled.
/// </summary>
[InterpolatedStringHandler]
public ref struct InfoInterpolatedStringHandler
{
    private SensitiveInterpolatedStringHandlerCore _inner;

    public InfoInterpolatedStringHandler(int literalLength, int formattedCount, ILogger logger, out bool shouldAppend)
    {
        shouldAppend = logger.IsInfo;
        _inner = shouldAppend ? new(literalLength, formattedCount) : default;
    }

    public void AppendLiteral(string value) => _inner.AppendLiteral(value);
    public void AppendFormatted<T>(T value) => _inner.AppendFormatted(value);
    public void AppendFormatted<T>(T value, int alignment) => _inner.AppendFormatted(value, alignment);
    public void AppendFormatted(string? value) => _inner.AppendFormatted(value);
    public void AppendFormatted(ReadOnlySpan<char> value) => _inner.AppendFormatted(value);

    public void AppendFormatted(ReadOnlySpan<char> value, int alignment = 0, string? format = null) => _inner.AppendFormatted(value, alignment, format);
    public void AppendFormatted<T>(T value, string? format) => _inner.AppendFormatted(value, format);
    public void AppendFormatted<T>(T value, int alignment, string? format) => _inner.AppendFormatted(value, alignment, format);

    public string ToStringAndClear() => _inner.ToStringAndClear();
}

/// <summary>
/// Interpolated string handler for <see cref="ILogger.Debug"/>, <see cref="ILogger.DebugError"/>, and <see cref="ILogger.DebugWarn"/>.
/// When <see cref="ILogger.IsDebug"/> is false the compiler skips all AppendLiteral/AppendFormatted
/// calls entirely, so the interpolation pays no allocation cost. Marked <c>:hide</c> fields are
/// masked when enabled. The caller method prepends any "DEBUG/ERROR: " or "DEBUG/WARN: " prefix.
/// </summary>
[InterpolatedStringHandler]
public ref struct DebugInterpolatedStringHandler
{
    private SensitiveInterpolatedStringHandlerCore _inner;

    public DebugInterpolatedStringHandler(int literalLength, int formattedCount, ILogger logger, out bool shouldAppend)
    {
        shouldAppend = logger.IsDebug;
        _inner = shouldAppend ? new(literalLength, formattedCount) : default;
    }

    public void AppendLiteral(string value) => _inner.AppendLiteral(value);
    public void AppendFormatted<T>(T value) => _inner.AppendFormatted(value);
    public void AppendFormatted<T>(T value, int alignment) => _inner.AppendFormatted(value, alignment);
    public void AppendFormatted<T>(T value, string? format) => _inner.AppendFormatted(value, format);
    public void AppendFormatted<T>(T value, int alignment, string? format) => _inner.AppendFormatted(value, alignment, format);
    public void AppendFormatted(string? value) => _inner.AppendFormatted(value);
    public void AppendFormatted(ReadOnlySpan<char> value) => _inner.AppendFormatted(value);
    public void AppendFormatted(ReadOnlySpan<char> value, int alignment = 0, string? format = null) => _inner.AppendFormatted(value, alignment, format);
    public string ToStringAndClear() => _inner.ToStringAndClear();
}

/// <summary>
/// Interpolated string handler for <see cref="ILogger.Trace"/>, <see cref="ILogger.TraceError"/>, and <see cref="ILogger.TraceWarn"/>.
/// Same shape as <see cref="DebugInterpolatedStringHandler"/> but gated on <see cref="ILogger.IsTrace"/>.
/// </summary>
[InterpolatedStringHandler]
public ref struct TraceInterpolatedStringHandler
{
    private SensitiveInterpolatedStringHandlerCore _inner;

    public TraceInterpolatedStringHandler(int literalLength, int formattedCount, ILogger logger, out bool shouldAppend)
    {
        shouldAppend = logger.IsTrace;
        _inner = shouldAppend ? new(literalLength, formattedCount) : default;
    }

    public void AppendLiteral(string value) => _inner.AppendLiteral(value);
    public void AppendFormatted<T>(T value) => _inner.AppendFormatted(value);
    public void AppendFormatted<T>(T value, int alignment) => _inner.AppendFormatted(value, alignment);
    public void AppendFormatted<T>(T value, string? format) => _inner.AppendFormatted(value, format);
    public void AppendFormatted<T>(T value, int alignment, string? format) => _inner.AppendFormatted(value, alignment, format);
    public void AppendFormatted(string? value) => _inner.AppendFormatted(value);
    public void AppendFormatted(ReadOnlySpan<char> value) => _inner.AppendFormatted(value);
    public void AppendFormatted(ReadOnlySpan<char> value, int alignment = 0, string? format = null) => _inner.AppendFormatted(value, alignment, format);
    public string ToStringAndClear() => _inner.ToStringAndClear();
}

/// <summary>Formats warning messages, masking fields marked with <c>:hide</c> when enabled.</summary>
[InterpolatedStringHandler]
public ref struct WarnInterpolatedStringHandler
{
    private SensitiveInterpolatedStringHandlerCore _inner;

    public WarnInterpolatedStringHandler(int literalLength, int formattedCount, ILogger logger, out bool shouldAppend)
    {
        shouldAppend = logger.IsWarn;
        _inner = shouldAppend ? new(literalLength, formattedCount) : default;
    }

    public void AppendLiteral(string value) => _inner.AppendLiteral(value);
    public void AppendFormatted<T>(T value) => _inner.AppendFormatted(value);
    public void AppendFormatted<T>(T value, int alignment) => _inner.AppendFormatted(value, alignment);
    public void AppendFormatted<T>(T value, string? format) => _inner.AppendFormatted(value, format);
    public void AppendFormatted<T>(T value, int alignment, string? format) => _inner.AppendFormatted(value, alignment, format);
    public void AppendFormatted(string? value) => _inner.AppendFormatted(value);
    public void AppendFormatted(ReadOnlySpan<char> value) => _inner.AppendFormatted(value);
    public void AppendFormatted(ReadOnlySpan<char> value, int alignment = 0, string? format = null) => _inner.AppendFormatted(value, alignment, format);
    public string ToStringAndClear() => _inner.ToStringAndClear();
}

/// <summary>Formats error messages, masking fields marked with <c>:hide</c> when enabled.</summary>
[InterpolatedStringHandler]
public ref struct ErrorInterpolatedStringHandler
{
    private SensitiveInterpolatedStringHandlerCore _inner;

    public ErrorInterpolatedStringHandler(int literalLength, int formattedCount, ILogger logger, out bool shouldAppend)
    {
        shouldAppend = logger.IsError;
        _inner = shouldAppend ? new(literalLength, formattedCount) : default;
    }

    public void AppendLiteral(string value) => _inner.AppendLiteral(value);
    public void AppendFormatted<T>(T value) => _inner.AppendFormatted(value);
    public void AppendFormatted<T>(T value, int alignment) => _inner.AppendFormatted(value, alignment);
    public void AppendFormatted<T>(T value, string? format) => _inner.AppendFormatted(value, format);
    public void AppendFormatted<T>(T value, int alignment, string? format) => _inner.AppendFormatted(value, alignment, format);
    public void AppendFormatted(string? value) => _inner.AppendFormatted(value);
    public void AppendFormatted(ReadOnlySpan<char> value) => _inner.AppendFormatted(value);
    public void AppendFormatted(ReadOnlySpan<char> value, int alignment = 0, string? format = null) => _inner.AppendFormatted(value, alignment, format);
    public string ToStringAndClear() => _inner.ToStringAndClear();
}

internal ref struct SensitiveInterpolatedStringHandlerCore(int literalLength, int formattedCount)
{
    private DefaultInterpolatedStringHandler _inner = new(literalLength, formattedCount);
    private readonly bool _maskSensitiveData = SensitiveLogMasking.Enabled;

    public void AppendLiteral(string value) => _inner.AppendLiteral(value);
    public void AppendFormatted<T>(T value) => _inner.AppendFormatted(value);
    public void AppendFormatted<T>(T value, int alignment) => _inner.AppendFormatted(value, alignment);
    public void AppendFormatted(string? value) => _inner.AppendFormatted(value);
    public void AppendFormatted(ReadOnlySpan<char> value) => _inner.AppendFormatted(value);

    public void AppendFormatted(ReadOnlySpan<char> value, int alignment, string? format)
    {
        if (format is "hide")
        {
            if (_maskSensitiveData) _inner.AppendFormatted("[redacted]", alignment);
            else _inner.AppendFormatted(value, alignment, null);
        }
        else
        {
            _inner.AppendFormatted(value, alignment, format);
        }
    }

    public void AppendFormatted<T>(T value, string? format)
    {
        if (format is "hide")
        {
            if (_maskSensitiveData) _inner.AppendLiteral("[redacted]");
            else _inner.AppendFormatted(value);
        }
        else
        {
            _inner.AppendFormatted(value, format);
        }
    }

    public void AppendFormatted<T>(T value, int alignment, string? format)
    {
        if (format is "hide")
        {
            if (_maskSensitiveData) _inner.AppendFormatted("[redacted]", alignment);
            else _inner.AppendFormatted(value, alignment);
        }
        else
        {
            _inner.AppendFormatted(value, alignment, format);
        }
    }

    public string ToStringAndClear() => _inner.ToStringAndClear();
}
