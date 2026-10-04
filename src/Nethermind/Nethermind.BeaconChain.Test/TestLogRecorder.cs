// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Logging;

namespace Nethermind.BeaconChain.Test;

[Flags]
internal enum TestLogLevels
{
    Error = 1,
    Warn = 2,
    Info = 4,
    Debug = 8,
    Trace = 16,
    All = Error | Warn | Info | Debug | Trace,
}

/// <summary>Records selected log methods or sends them to a fixture's sink.</summary>
/// <remarks>Enabled flags govern lazy callers; overriding a flag does not change which direct method calls are recorded.</remarks>
internal sealed class TestLogRecorder(TestLogLevels levels = TestLogLevels.All, Action<LogLevel, string, Exception?>? sink = null) : InterfaceLogger, ILogManager
{
    private readonly ConcurrentQueue<(string Level, string Text)> _lines = new();

    public IReadOnlyCollection<(string Level, string Text)> Lines => _lines;
    public string[] Messages => [.. _lines.Select(static line => line.Text)];

    public bool IsInfo { get; init; } = (levels & TestLogLevels.Info) != 0;
    public bool IsWarn { get; init; } = (levels & TestLogLevels.Warn) != 0;
    public bool IsDebug { get; init; } = (levels & TestLogLevels.Debug) != 0;
    public bool IsTrace { get; init; } = (levels & TestLogLevels.Trace) != 0;
    public bool IsError { get; init; } = (levels & TestLogLevels.Error) != 0;

    public ILogger GetLogger(string loggerName) => new(this);
    public ILogger GetClassLogger<T>() => GetLogger(typeof(T).Name);

    public void Info(string text) => Write(LogLevel.Info, text);
    public void Warn(string text) => Write(LogLevel.Warn, text);
    public void Debug(string text) => Write(LogLevel.Debug, text);
    public void Trace(string text) => Write(LogLevel.Trace, text);
    public void Error(string text, Exception? ex = null) => Write(LogLevel.Error, text, ex);

    private void Write(LogLevel level, string text, Exception? exception = null)
    {
        if ((levels & (TestLogLevels)(1 << (int)level)) == 0) return;
        if (sink is null) _lines.Enqueue((level.ToString(), text));
        else sink(level, text, exception);
    }
}
