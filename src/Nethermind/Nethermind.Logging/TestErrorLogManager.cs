// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace Nethermind.Logging;

public class TestErrorLogManager : ILogManager
{
    private readonly ConcurrentQueue<Error> _errors = new();

    public IReadOnlyCollection<Error> Errors => _errors;

    public ILogger GetClassLogger<T>() => new(new TestErrorLogger(_errors));

    public ILogger GetLogger(string loggerName) => new(new TestErrorLogger(_errors));

    public class TestErrorLogger(ConcurrentQueue<TestErrorLogManager.Error> errors) : InterfaceLogger
    {
        private readonly ConcurrentQueue<Error> _errors = errors;

        public void Info(string text) { }
        public void Warn(string text) { }
        public void Debug(string text) { }
        public void Trace(string text) { }
        public void Error(string text, Exception? ex = null) => _errors.Enqueue(new Error() { Text = text, Exception = ex });
        public bool IsInfo => false;
        public bool IsWarn => false;
        public bool IsDebug => true;
        public bool IsTrace => false;
        public bool IsError => true;
    }

    public record Error
    {
        public required string Text { get; init; }
        public Exception? Exception { get; init; }
    }
}
