// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Logging
{
    public class OneLoggerLogManager : ILogManager
    {
        private readonly Logger _logger;

        public OneLoggerLogManager(Logger logger)
        {
            _logger = logger;
        }

        public Logger GetClassLogger(Type type) => _logger;

        public Logger GetClassLogger<T>() => _logger;

        public Logger GetClassLogger() => _logger;

        public Logger GetLogger(string loggerName) => _logger;
    }
}
