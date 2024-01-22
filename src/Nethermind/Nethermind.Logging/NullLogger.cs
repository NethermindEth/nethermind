// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Logging
{
    public class NullLogger : InterfaceLogger
    {
        public static ILogger Instance => default;

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
