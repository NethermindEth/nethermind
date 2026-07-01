// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Microsoft.Extensions.Logging;
using MicrosoftLogger = Microsoft.Extensions.Logging.ILogger;
using MsLogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace Nethermind.Logging.Microsoft;

public sealed class NethermindLoggerFactory(ILogManager logManager, bool lowerLogLevel = false, MsLogLevel? maxLogLevel = null) : ILoggerFactory
{
    public MicrosoftLogger CreateLogger(string categoryName)
    {
        string loggerName = categoryName.Replace("Nethermind.", string.Empty);
        return new NethermindLoggerAdapter(logManager.GetLogger(loggerName), lowerLogLevel, maxLogLevel);
    }

    public void Dispose() { }

    public void AddProvider(ILoggerProvider provider) { }

    private sealed class NethermindLoggerAdapter(ILogger logger, bool lowerLogLevel = false, MsLogLevel? maxLogLevel = null) : MicrosoftLogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(MsLogLevel logLevel)
        {
            if (lowerLogLevel)
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
            if (lowerLogLevel)
            {
                logLevel = LowerLogLevel(logLevel, maxLogLevel);
            }

            switch (logLevel)
            {
                case MsLogLevel.Critical:
                case MsLogLevel.Error:
                    if (logger.IsError)
                    {
                        logger.Error(formatter(state, exception), exception);
                    }
                    break;
                case MsLogLevel.Warning:
                    if (logger.IsWarn)
                    {
                        logger.Warn(formatter(state, exception));
                    }
                    break;
                case MsLogLevel.Information:
                    if (logger.IsInfo)
                    {
                        logger.Info(formatter(state, exception));
                    }
                    break;
                case MsLogLevel.Debug:
                    if (logger.IsDebug)
                    {
                        logger.Debug(formatter(state, exception));
                    }
                    break;
                case MsLogLevel.Trace:
                    if (logger.IsTrace)
                    {
                        logger.Trace(formatter(state, exception));
                    }
                    break;
            }
        }

        private static MsLogLevel LowerLogLevel(MsLogLevel logLevel, MsLogLevel? maxLogLevel)
        {
            // DotNetty outputs Trace level data at Info.
            MsLogLevel loweredLogLevel = logLevel switch
            {
                MsLogLevel.Critical => MsLogLevel.Error,
                MsLogLevel.Error => MsLogLevel.Warning,
                MsLogLevel.Warning => MsLogLevel.Information,
                MsLogLevel.Information => MsLogLevel.Trace,
                MsLogLevel.Debug => MsLogLevel.Trace,
                _ => logLevel,
            };

            return maxLogLevel is not null && loweredLogLevel > maxLogLevel.Value ? maxLogLevel.Value : loweredLogLevel;
        }
    }
}
