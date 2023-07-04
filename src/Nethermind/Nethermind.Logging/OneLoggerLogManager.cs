// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Logging
{
    public class OneLoggerLogManager : ILogManager
    {
        private readonly ILogger _logger;

        public OneLoggerLogManager(ILogger logger)
        {
            _logger = logger;
        }

        public ILogger GetClassLogger(Type type)
        {
            return _logger;
        }

        public ILogger GetClassLogger<T>()
        {
            return _logger;
        }

        public ILogger GetClassLogger()
        {
            return _logger;
        }

        public ILogger GetLogger(string loggerName)
        {
            return _logger;
        }
    }
}
