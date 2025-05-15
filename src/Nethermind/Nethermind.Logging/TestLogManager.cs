// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;

namespace Nethermind.Logging
{
    public class TestLogManager : ILogManager
    {
        public static readonly TestLogManager Instance = new TestLogManager();
        private readonly NUnitLogger _logger;

        public TestLogManager(LogLevel level = LogLevel.Info)
        {
            _logger = new NUnitLogger(level);
        }

        public ILogger GetClassLogger(Type type) => GetClassLogger();

        public ILogger GetClassLogger<T>() => GetClassLogger();

        public ILogger GetClassLogger([CallerFilePath] string filePath = "") => new(_logger);

        public ILogger GetLogger(string loggerName) => GetClassLogger();

        private class NUnitLogger(LogLevel level) : InterfaceLogger
        {
            public void Info(string text)
            {
                if (IsInfo)
                {
                    Log(text);
                }
            }

            public void Warn(string text)
            {
                if (IsWarn)
                {
                    Log(text);
                }
            }

            public void Debug(string text)
            {
                if (IsDebug)
                {
                    Log(text);
                }
            }

            public void Trace(string text)
            {
                if (IsTrace)
                {
                    Log(text);
                }
            }

            public void Error(string text, Exception ex = null)
            {
                if (IsError)
                {
                    Log(text, ex);
                }
            }

            public bool IsInfo => CheckLevel(LogLevel.Info);
            public bool IsWarn => CheckLevel(LogLevel.Warn);
            public bool IsDebug => CheckLevel(LogLevel.Debug);
            public bool IsTrace => CheckLevel(LogLevel.Trace);
            public bool IsError => CheckLevel(LogLevel.Error);

            private bool CheckLevel(LogLevel logLevel) => level >= logLevel;

            private static void Log(string text, Exception ex = null)
            {
                Console.Error.WriteLine(text);

                if (ex is not null)
                {
                    Console.Error.WriteLine(ex.ToString());
                }
            }
        }
    }
}
