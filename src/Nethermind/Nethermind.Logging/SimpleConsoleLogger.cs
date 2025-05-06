// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Logging
{
    /// <summary>
    /// Just use before the logger is configured
    /// </summary>
    public class SimpleConsoleLogger : InterfaceLogger
    {
        private SimpleConsoleLogger()
        {
        }

        public static SimpleConsoleLogger Instance { get; } = new SimpleConsoleLogger();

        public void Info(string text)
        {
            WriteEntry(text);
        }

        public void Warn(string text)
        {
            WriteEntry(text);
        }

        public void Debug(string text)
        {
            WriteEntry(text);
        }

        public void Trace(string text)
        {
            WriteEntry(text);
        }

        public void Error(string text, Exception ex = null)
        {
            WriteEntry(text + " " + ex);
        }

        private static void WriteEntry(string text)
        {
            Console.Error.WriteLine(DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss.ffff|") + text);
        }

        public bool IsInfo => true;
        public bool IsWarn => true;
        public bool IsDebug => true;
        public bool IsTrace => true;
        public bool IsError => true;
    }
}
