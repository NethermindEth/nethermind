// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;

namespace Nethermind.Logging;

/// <summary>
/// Interpolated string handler for <see cref="ILogger.DebugError"/> / <see cref="ILogger.DebugWarn"/>.
/// When <see cref="ILogger.IsDebug"/> is false the compiler skips all AppendLiteral/AppendFormatted
/// calls entirely, so the interpolation pays no allocation cost. The caller method is responsible
/// for prepending the "DEBUG/ERROR: " or "DEBUG/WARN: " prefix; the handler stays severity-agnostic.
/// </summary>
[InterpolatedStringHandler]
public ref struct DebugInterpolatedStringHandler
{
    private DefaultInterpolatedStringHandler _inner;

    public DebugInterpolatedStringHandler(int literalLength, int formattedCount, ILogger logger, out bool shouldAppend)
    {
        shouldAppend = logger.IsDebug;
        _inner = shouldAppend
            ? new DefaultInterpolatedStringHandler(literalLength, formattedCount)
            : default;
    }

    public void AppendLiteral(string value) => _inner.AppendLiteral(value);
    public void AppendFormatted<T>(T value) => _inner.AppendFormatted(value);
    public void AppendFormatted<T>(T value, string format) => _inner.AppendFormatted(value, format);
    public void AppendFormatted(string value) => _inner.AppendFormatted(value);
    public void AppendFormatted(ReadOnlySpan<char> value) => _inner.AppendFormatted(value);
    public string ToStringAndClear() => _inner.ToStringAndClear();
}

/// <summary>
/// Interpolated string handler for <see cref="ILogger.TraceError"/> / <see cref="ILogger.TraceWarn"/>.
/// Same shape as <see cref="DebugInterpolatedStringHandler"/> but gated on <see cref="ILogger.IsTrace"/>.
/// </summary>
[InterpolatedStringHandler]
public ref struct TraceInterpolatedStringHandler
{
    private DefaultInterpolatedStringHandler _inner;

    public TraceInterpolatedStringHandler(int literalLength, int formattedCount, ILogger logger, out bool shouldAppend)
    {
        shouldAppend = logger.IsTrace;
        _inner = shouldAppend
            ? new DefaultInterpolatedStringHandler(literalLength, formattedCount)
            : default;
    }

    public void AppendLiteral(string value) => _inner.AppendLiteral(value);
    public void AppendFormatted<T>(T value) => _inner.AppendFormatted(value);
    public void AppendFormatted<T>(T value, string format) => _inner.AppendFormatted(value, format);
    public void AppendFormatted(string value) => _inner.AppendFormatted(value);
    public void AppendFormatted(ReadOnlySpan<char> value) => _inner.AppendFormatted(value);
    public string ToStringAndClear() => _inner.ToStringAndClear();
}
