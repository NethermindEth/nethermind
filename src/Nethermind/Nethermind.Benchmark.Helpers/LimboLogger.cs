// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Microsoft.Extensions.Logging;
using Nethermind.Core2;

namespace Nethermind.Benchmark.Helpers
{
    public class LimboLogger<T> : ILogger<T>
    {
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
        {
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public IDisposable BeginScope<TState>(TState state)
        {
            return EmptyDisposable.Instance;
        }
    }

    public class LimboLogger
    {
        public ILogger<T> Get<T>()
        {
            return new LimboLogger<T>();
        }
    }
}
