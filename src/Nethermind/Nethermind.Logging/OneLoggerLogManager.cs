// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Logging
{
    public class OneLoggerLogManager(ILogger logger) : ILogManager
    {
        private readonly ILogger _logger = logger;

        public ILogger GetClassLogger<T>() => _logger;

        public ILogger GetLogger(string loggerName) => _logger;
    }
}
