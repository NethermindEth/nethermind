// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;

namespace Nethermind.Logging
{
    public class SimpleConsoleLogManager(LogLevel logLevel = LogLevel.Trace, string dateFormat = "yyyy-MM-dd HH-mm-ss.ffff|") : ILogManager
    {
        public static ILogManager Instance { get; } = new SimpleConsoleLogManager();

        public ILogger GetClassLogger<T>()
        {
            return new(new SimpleConsoleLogger(logLevel, dateFormat));
        }

        public ILogger GetClassLogger([CallerFilePath] string filePath = "")
        {
            return new(new SimpleConsoleLogger(logLevel, dateFormat));
        }

        public ILogger GetLogger(string loggerName)
        {
            return new(new SimpleConsoleLogger(logLevel, dateFormat));
        }
    }
}
