// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Microsoft.Extensions.Logging;

namespace Nethermind.Runner.Logging
{
    public class CustomMicrosoftLogger : ILogger
    {
        private readonly Nethermind.Logging.ILogger _logger;

        public CustomMicrosoftLogger(Nethermind.Logging.ILogger logger)
        {
            _logger = logger;
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
        {
            if (!IsLevelEnabled(logLevel))
            {
                return;
            }

            if (formatter is null)
            {
                throw new ArgumentNullException(nameof(formatter));
            }

            var message = formatter(state, exception);
            switch (logLevel)
            {
                case LogLevel.Error:
                case LogLevel.Critical:
                    _logger.Error(message, exception);
                    break;
                case LogLevel.Information:
                    _logger.Info(message);
                    break;
                case LogLevel.Warning:
                    _logger.Warn(message);
                    break;
                case LogLevel.Debug:
                    _logger.Debug(message);
                    break;
                case LogLevel.Trace:
                    _logger.Trace(message);
                    break;
            }
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return IsLevelEnabled(logLevel);
        }

        public IDisposable BeginScope<TState>(TState state)
        {
            return NullScope.Instance;
        }

        private bool IsLevelEnabled(LogLevel logLevel)
        {
            switch (logLevel)
            {
                case LogLevel.Error:
                case LogLevel.Critical:
                    return _logger.IsError;
                case LogLevel.Information:
                    return _logger.IsInfo;
                case LogLevel.Warning:
                    return _logger.IsWarn;
                case LogLevel.Debug:
                    return _logger.IsDebug;
                case LogLevel.Trace:
                    return _logger.IsTrace;
                default:
                    return false;
            }
        }

        private class NullScope : IDisposable
        {
            public static NullScope Instance { get; } = new NullScope();

            private NullScope()
            {
            }

            public void Dispose()
            {
            }
        }
    }
}
