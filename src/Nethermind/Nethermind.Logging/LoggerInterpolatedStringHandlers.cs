// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;

namespace Nethermind.Logging;

/// <summary>
/// Interpolated string handler for <see cref="ILogger.DebugError"/>. When <see cref="ILogger.IsDebug"/> is
/// false the compiler skips all AppendLiteral/AppendFormatted calls, so the interpolation pays no
/// allocation cost. When true, the result is prefixed with "DEBUG/ERROR: " before being logged.
/// </summary>
[InterpolatedStringHandler]
public ref struct DebugErrorInterpolatedStringHandler
{
    private const string Prefix = "DEBUG/ERROR: ";

    private DefaultInterpolatedStringHandler _inner;
    private readonly bool _enabled;

    public DebugErrorInterpolatedStringHandler(int literalLength, int formattedCount, ILogger logger, out bool shouldAppend)
    {
        _enabled = shouldAppend = logger.IsDebug;
        if (_enabled)
        {
            _inner = new DefaultInterpolatedStringHandler(literalLength + Prefix.Length, formattedCount);
            _inner.AppendLiteral(Prefix);
        }
        else
        {
            _inner = default;
        }
    }

    public void AppendLiteral(string value)
    {
        if (_enabled) _inner.AppendLiteral(value);
    }

    public void AppendFormatted<T>(T value)
    {
        if (_enabled) _inner.AppendFormatted(value);
    }

    public void AppendFormatted<T>(T value, string format)
    {
        if (_enabled) _inner.AppendFormatted(value, format);
    }

    public void AppendFormatted(string value)
    {
        if (_enabled) _inner.AppendFormatted(value);
    }

    public string ToStringAndClear() => _enabled ? _inner.ToStringAndClear() : string.Empty;
}

/// <summary>
/// Interpolated string handler for <see cref="ILogger.DebugWarn"/>. When <see cref="ILogger.IsDebug"/> is
/// false the compiler skips all AppendLiteral/AppendFormatted calls, so the interpolation pays no
/// allocation cost. When true, the result is prefixed with "DEBUG/WARN: " before being logged.
/// </summary>
[InterpolatedStringHandler]
public ref struct DebugWarnInterpolatedStringHandler
{
    private const string Prefix = "DEBUG/WARN: ";

    private DefaultInterpolatedStringHandler _inner;
    private readonly bool _enabled;

    public DebugWarnInterpolatedStringHandler(int literalLength, int formattedCount, ILogger logger, out bool shouldAppend)
    {
        _enabled = shouldAppend = logger.IsDebug;
        if (_enabled)
        {
            _inner = new DefaultInterpolatedStringHandler(literalLength + Prefix.Length, formattedCount);
            _inner.AppendLiteral(Prefix);
        }
        else
        {
            _inner = default;
        }
    }

    public void AppendLiteral(string value)
    {
        if (_enabled) _inner.AppendLiteral(value);
    }

    public void AppendFormatted<T>(T value)
    {
        if (_enabled) _inner.AppendFormatted(value);
    }

    public void AppendFormatted<T>(T value, string format)
    {
        if (_enabled) _inner.AppendFormatted(value, format);
    }

    public void AppendFormatted(string value)
    {
        if (_enabled) _inner.AppendFormatted(value);
    }

    public string ToStringAndClear() => _enabled ? _inner.ToStringAndClear() : string.Empty;
}

/// <summary>
/// Interpolated string handler for <see cref="ILogger.TraceError"/>. When <see cref="ILogger.IsTrace"/> is
/// false the compiler skips all AppendLiteral/AppendFormatted calls, so the interpolation pays no
/// allocation cost. When true, the result is prefixed with "TRACE/ERROR: " before being logged.
/// </summary>
[InterpolatedStringHandler]
public ref struct TraceErrorInterpolatedStringHandler
{
    private const string Prefix = "TRACE/ERROR: ";

    private DefaultInterpolatedStringHandler _inner;
    private readonly bool _enabled;

    public TraceErrorInterpolatedStringHandler(int literalLength, int formattedCount, ILogger logger, out bool shouldAppend)
    {
        _enabled = shouldAppend = logger.IsTrace;
        if (_enabled)
        {
            _inner = new DefaultInterpolatedStringHandler(literalLength + Prefix.Length, formattedCount);
            _inner.AppendLiteral(Prefix);
        }
        else
        {
            _inner = default;
        }
    }

    public void AppendLiteral(string value)
    {
        if (_enabled) _inner.AppendLiteral(value);
    }

    public void AppendFormatted<T>(T value)
    {
        if (_enabled) _inner.AppendFormatted(value);
    }

    public void AppendFormatted<T>(T value, string format)
    {
        if (_enabled) _inner.AppendFormatted(value, format);
    }

    public void AppendFormatted(string value)
    {
        if (_enabled) _inner.AppendFormatted(value);
    }

    public string ToStringAndClear() => _enabled ? _inner.ToStringAndClear() : string.Empty;
}

/// <summary>
/// Interpolated string handler for <see cref="ILogger.TraceWarn"/>. When <see cref="ILogger.IsTrace"/> is
/// false the compiler skips all AppendLiteral/AppendFormatted calls, so the interpolation pays no
/// allocation cost. When true, the result is prefixed with "TRACE/WARN: " before being logged.
/// </summary>
[InterpolatedStringHandler]
public ref struct TraceWarnInterpolatedStringHandler
{
    private const string Prefix = "TRACE/WARN: ";

    private DefaultInterpolatedStringHandler _inner;
    private readonly bool _enabled;

    public TraceWarnInterpolatedStringHandler(int literalLength, int formattedCount, ILogger logger, out bool shouldAppend)
    {
        _enabled = shouldAppend = logger.IsTrace;
        if (_enabled)
        {
            _inner = new DefaultInterpolatedStringHandler(literalLength + Prefix.Length, formattedCount);
            _inner.AppendLiteral(Prefix);
        }
        else
        {
            _inner = default;
        }
    }

    public void AppendLiteral(string value)
    {
        if (_enabled) _inner.AppendLiteral(value);
    }

    public void AppendFormatted<T>(T value)
    {
        if (_enabled) _inner.AppendFormatted(value);
    }

    public void AppendFormatted<T>(T value, string format)
    {
        if (_enabled) _inner.AppendFormatted(value, format);
    }

    public void AppendFormatted(string value)
    {
        if (_enabled) _inner.AppendFormatted(value);
    }

    public string ToStringAndClear() => _enabled ? _inner.ToStringAndClear() : string.Empty;
}
