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

    public Logger GetClassLogger(Type type) => GetClassLogger();

    public Logger GetClassLogger<T>() => GetClassLogger();

    public Logger GetClassLogger() => new(new TestErrorLogger(_errors));

    public Logger GetLogger(string loggerName) => GetClassLogger();

    public class TestErrorLogger : ILogger
    {
        private readonly ConcurrentQueue<Error> _errors;

        public TestErrorLogger(ConcurrentQueue<Error> errors)
        {
            _errors = errors;
        }

        public void Info(string text) { }
        public void Warn(string text) { }
        public void Debug(string text) { }
        public void Trace(string text) { }
        public void Error(string text, Exception ex = null)
        {
            _errors.Enqueue(new Error() { Text = text, Exception = ex });
        }
        public bool IsInfo => false;
        public bool IsWarn => false;
        public bool IsDebug => true;
        public bool IsTrace => false;
        public bool IsError => true;
    }

    public record Error
    {
        public string Text { get; init; }
        public Exception Exception { get; init; }
    }
}
