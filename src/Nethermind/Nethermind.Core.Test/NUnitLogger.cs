// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Logging;

namespace Nethermind.Core.Test
{
    public class NUnitLogger(LogLevel level) : InterfaceLogger
    {
        private readonly LogLevel _level = level;

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

        public void Error(string text, Exception? ex = null)
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

        private bool CheckLevel(LogLevel logLevel) => _level >= logLevel;

        private void Log(string text, Exception? ex = null)
        {
            //if (logToError) Console.Error.WriteLine(text); else Console.WriteLine(text);
            // TestContext.Out.WriteLine(text);

            //if (ex is not null)
            //{
            //    TestContext.Out.WriteLine(ex.ToString());
            //}
        }
    }
}
