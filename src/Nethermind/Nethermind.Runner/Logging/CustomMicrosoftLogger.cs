//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

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

            if (formatter == null)
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
