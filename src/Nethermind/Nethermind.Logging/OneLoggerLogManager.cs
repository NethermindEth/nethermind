// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;

namespace Nethermind.Logging
{
    public class OneLoggerLogManager(ILogger logger) : ILogManager
    {
        public ILogger GetClassLogger<T>() => logger;

        public ILogger GetClassLogger([CallerFilePath] string filePath = "") => logger;

        public ILogger GetLogger(string loggerName) => logger;
    }
}
