// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
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

        public Logger GetClassLogger(Type type) => GetClassLogger();

        public Logger GetClassLogger<T>() => GetClassLogger();

        public Logger GetClassLogger() => new(_logger);

        public Logger GetLogger(string loggerName) => GetClassLogger();
    }
}
