// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Microsoft.Extensions.Logging;

namespace Nethermind.Runner.Logging
{
    public class CustomMicrosoftLogger : ILogger
    {
        private readonly Nethermind.Logging.ILogger _logger;

        public CustomMicrosoftLogger(in Nethermind.Logging.ILogger logger)
        {
            _logger = logger;
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
        {
            if (!IsLevelEnabled(logLevel))
            {
                return;
            }

            ArgumentNullException.ThrowIfNull(formatter);

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
            return logLevel switch
            {
                LogLevel.Error or LogLevel.Critical => _logger.IsError,
                LogLevel.Information => _logger.IsInfo,
                LogLevel.Warning => _logger.IsWarn,
                LogLevel.Debug => _logger.IsDebug,
                LogLevel.Trace => _logger.IsTrace,
                _ => false,
            };
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
