// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Microsoft.Extensions.Logging;
using Nethermind.Logging;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace Nethermind.Runner.Logging
{
    public class CustomMicrosoftLoggerProvider : ILoggerProvider
    {
        private readonly ILogManager _logManager;
        private const string WebApiLogNamePrefix = "JsonWebAPI";

        public CustomMicrosoftLoggerProvider(ILogManager logManager)
        {
            _logManager = logManager;
        }

        public ILogger CreateLogger(string categoryName)
        {
            var coreLogger = _logManager.GetLogger($"{WebApiLogNamePrefix}.{categoryName}");
            var customLogger = new CustomMicrosoftLogger(coreLogger);
            return customLogger;
        }

        public void Dispose()
        {
        }
    }
}
