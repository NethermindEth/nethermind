// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Microsoft.Extensions.Logging;
using Nethermind.Logging;
using MsLogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace Nethermind.Network.Discovery;

public sealed class NethermindLoggerFactory(ILogManager logManager, bool lowerLogLevel = false) : ILoggerFactory
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

        public bool IsEnabled(MsLogLevel logLevel)
        {
            if (lowerLogLevel)
            {
                if (logLevel <= MsLogLevel.Information)
                {
                    // DotNetty outputs Trace level data at Info
                    logLevel = MsLogLevel.Trace;
                }
                else
                {
                    logLevel = logLevel - 1;
                }
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
                if (logLevel <= MsLogLevel.Information)
                {
                    logLevel = MsLogLevel.Trace;
                }
                else
                {
                    logLevel = logLevel - 1;
                }
            }

            switch (logLevel)
            {
                case MsLogLevel.Critical:
                case MsLogLevel.Error:
                    if (logger.IsError)
                        logger.Error(formatter(state, exception));
                    break;
                case MsLogLevel.Warning:
                    if (logger.IsWarn)
                        logger.Warn(formatter(state, exception));
                    break;
                case MsLogLevel.Information:
                    if (logger.IsInfo)
                        logger.Info(formatter(state, exception));
                    break;
                case MsLogLevel.Debug:
                    if (logger.IsDebug)
                        logger.Debug(formatter(state, exception));
                    break;
                case MsLogLevel.Trace:
                    if (logger.IsTrace)
                        logger.Trace(formatter(state, exception));
                    break;
            }
        }
    }
}
