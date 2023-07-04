// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Logging;

namespace Nethermind.Core.Test
{
    public class NUnitLogManager : ILogManager
    {
        public static readonly NUnitLogManager Instance = new();
        private readonly NUnitLogger _logger;

        public NUnitLogManager(LogLevel level = LogLevel.Info)
        {
            _logger = new NUnitLogger(level);
        }

        public ILogger GetClassLogger(Type type) => GetClassLogger();

        public ILogger GetClassLogger<T>() => GetClassLogger();

        public ILogger GetClassLogger() => _logger;

        public ILogger GetLogger(string loggerName) => GetClassLogger();
    }
}
