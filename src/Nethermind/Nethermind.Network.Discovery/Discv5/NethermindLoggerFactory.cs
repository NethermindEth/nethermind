// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Microsoft.Extensions.Logging;
using Nethermind.Logging;

namespace Nethermind.Network.Discovery;

internal class NethermindLoggerFactory(ILogManager logManager, bool lowerLogLevel = false) : ILoggerFactory
{
    public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName)
    {
        return new NethermindLogger(logManager.GetLogger(categoryName), lowerLogLevel);
    }

    public void Dispose() { }

    public void AddProvider(ILoggerProvider provider) { }

    class NethermindLogger(Logging.ILogger logger, bool lowerLogLevel = false) : Microsoft.Extensions.Logging.ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, EventId eventId,
                                TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (lowerLogLevel && logLevel > Microsoft.Extensions.Logging.LogLevel.Debug)
            {
                logLevel = Microsoft.Extensions.Logging.LogLevel.Debug;
            }

            switch (logLevel)
            {
                case Microsoft.Extensions.Logging.LogLevel.Critical:
                case Microsoft.Extensions.Logging.LogLevel.Error:
                    if (logger.IsError) logger.Error(formatter(state, exception));
                    break;
                case Microsoft.Extensions.Logging.LogLevel.Warning:
                    if (logger.IsWarn) logger.Warn(formatter(state, exception));
                    break;
                case Microsoft.Extensions.Logging.LogLevel.Information:
                    if (logger.IsInfo) logger.Info(formatter(state, exception));
                    break;
                case Microsoft.Extensions.Logging.LogLevel.Debug:
                    if (logger.IsDebug) logger.Debug(formatter(state, exception));
                    break;
                case Microsoft.Extensions.Logging.LogLevel.Trace:
                    if (logger.IsTrace) logger.Trace(formatter(state, exception));
                    break;
            }
        }
    }
}
