// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Nethermind.Logging;

namespace Nethermind.State.Flat.Rust.Test;

/// <summary>Keeps every line logged at or above its level as <c>Level Logger: text</c>, in order.</summary>
internal sealed class RecordingLogManager(LogLevel level) : ILogManager
{
    private readonly ConcurrentQueue<string> _lines = new();

    public IReadOnlyCollection<string> Lines => _lines;

    public ILogger GetClassLogger<T>() => GetLogger(ILogManager.GetLoggerName(typeof(T)));

    public ILogger GetLogger(string loggerName) => new(new Logger(loggerName, level, _lines));

    private sealed class Logger(string name, LogLevel level, ConcurrentQueue<string> lines) : InterfaceLogger
    {
        public void Info(string text) => lines.Enqueue($"Info {name}: {text}");
        public void Warn(string text) => lines.Enqueue($"Warn {name}: {text}");
        public void Debug(string text) => lines.Enqueue($"Debug {name}: {text}");
        public void Trace(string text) => lines.Enqueue($"Trace {name}: {text}");
        public void Error(string text, Exception? ex = null) => lines.Enqueue($"Error {name}: {text}");
        public bool IsInfo => level >= LogLevel.Info;
        public bool IsWarn => level >= LogLevel.Warn;
        public bool IsDebug => level >= LogLevel.Debug;
        public bool IsTrace => level >= LogLevel.Trace;
        public bool IsError => true;
    }
}
