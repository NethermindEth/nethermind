// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Microsoft.Extensions.Logging;
using Nethermind.Logging;
using MsLogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace Nethermind.Network.Discovery;

public sealed class NethermindLoggerFactory(ILogManager logManager, bool lowerLogLevel = false, MsLogLevel? maxLogLevel = null) : ILoggerFactory
{
    public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName)
    {
        return new NethermindLogger(logManager.GetLogger(categoryName), lowerLogLevel, maxLogLevel);
    }

    public void Dispose() { }

    public void AddProvider(ILoggerProvider provider) { }

    class NethermindLogger(Logging.ILogger logger, bool lowerLogLevel = false, MsLogLevel? maxLogLevel = null) : Microsoft.Extensions.Logging.ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(MsLogLevel logLevel)
        {
            if (lowerLogLevel && logLevel > MsLogLevel.Debug)
            {
                logLevel = LowerLogLevel(logLevel, maxLogLevel);
            }

            return logLevel switch
            {
                MsLogLevel.Critical or MsLogLevel.Error => logger.IsError,
                MsLogLevel.Warning => logger.IsWarn,
                MsLogLevel.Information => logger.IsInfo,
                MsLogLevel.Debug => logger.IsDebug,
                MsLogLevel.Trace => logger.IsTrace,
                _ => false,
            };
        }

        public void Log<TState>(MsLogLevel logLevel, EventId eventId,
                                TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (lowerLogLevel && logLevel > MsLogLevel.Debug)
            {
                logLevel = LowerLogLevel(logLevel, maxLogLevel);
            }

            switch (logLevel)
            {
                case MsLogLevel.Critical:
                case MsLogLevel.Error:
                    logger.Error(formatter(state, exception));
                    break;
                case MsLogLevel.Warning:
                    logger.Warn(formatter(state, exception));
                    break;
                case MsLogLevel.Information:
                    logger.Info(formatter(state, exception));
                    break;
                case MsLogLevel.Debug:
                    logger.Debug(formatter(state, exception));
                    break;
                case MsLogLevel.Trace:
                    logger.Trace(formatter(state, exception));
                    break;
            }
        }

        private static MsLogLevel LowerLogLevel(MsLogLevel logLevel, MsLogLevel? maxLogLevel)
        {
            // DotNetty outputs Trace level data at Info
            MsLogLevel loweredLogLevel = logLevel switch
            {
                MsLogLevel.Critical => MsLogLevel.Error,
                MsLogLevel.Error => MsLogLevel.Warning,
                MsLogLevel.Warning => MsLogLevel.Information,
                MsLogLevel.Information => MsLogLevel.Trace,
                MsLogLevel.Debug => MsLogLevel.Trace,
                _ => logLevel,
            };

            return loweredLogLevel > maxLogLevel ? maxLogLevel.Value : loweredLogLevel;
        }
    }
}
