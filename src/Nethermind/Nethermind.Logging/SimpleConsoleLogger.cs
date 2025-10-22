// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Logging
{
    /// <summary>
    /// Just use before the logger is configured
    /// </summary>
    public class SimpleConsoleLogger(LogLevel logLevel = LogLevel.Trace, string dateFormat = "yyyy-MM-dd HH-mm-ss.ffff|") : InterfaceLogger
    {
        public static SimpleConsoleLogger Instance { get; } = new();

        public void Info(string text)
        {
            if (IsInfo) WriteEntry(text);
        }

        public void Warn(string text)
        {
            if (IsWarn) WriteEntry(text);
        }

        public void Debug(string text)
        {
            if (IsDebug) WriteEntry(text);
        }

        public void Trace(string text)
        {
            if (IsTrace) WriteEntry(text);
        }

        public void Error(string text, Exception ex = null)
        {
            if (IsError) Console.Error.WriteLine(DateTime.Now.ToString(dateFormat) + text + (ex != null ? " " + ex : string.Empty));
        }

        private void WriteEntry(string text)
        {
            Console.Out.WriteLine(DateTime.Now.ToString(dateFormat) + text);
        }

        public bool IsInfo => logLevel >= LogLevel.Info;
        public bool IsWarn => logLevel >= LogLevel.Warn;
        public bool IsDebug => logLevel >= LogLevel.Debug;
        public bool IsTrace => logLevel >= LogLevel.Trace;
        public bool IsError => logLevel >= LogLevel.Error;
    }
}
