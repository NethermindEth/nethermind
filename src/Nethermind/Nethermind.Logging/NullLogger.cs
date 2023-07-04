// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;

namespace Nethermind.Logging
{
    public class NullLogger : ILogger
    {
        private static NullLogger _instance;

        public static NullLogger Instance
        {
            get { return LazyInitializer.EnsureInitialized(ref _instance, () => new NullLogger()); }
        }

        private NullLogger()
        {
        }

        public void Info(string text)
        {
        }

        public void Warn(string text)
        {
        }

        public void Debug(string text)
        {
        }

        public void Trace(string text)
        {
        }

        public void Error(string text, Exception ex = null)
        {
        }

        public bool IsInfo { get; } = false;
        public bool IsWarn { get; } = false;
        public bool IsDebug { get; } = false;
        public bool IsTrace { get; } = false;
        public bool IsError { get; } = false;
    }
}
