// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Logging;

namespace Nethermind.Core.Test
{
    public class TestLogger : ILogger
    {
        public List<string> LogList { get; set; } = new();

        public void Info(string text)
        {
            LogList.Add(text);
        }

        public void Warn(string text)
        {
            LogList.Add(text);
        }

        public void Debug(string text)
        {
            LogList.Add(text);
        }

        public void Trace(string text)
        {
            LogList.Add(text);
        }

        public void Error(string text, Exception? ex = null)
        {
            LogList.Add(text);
        }

        public bool IsInfo { get; set; } = true;
        public bool IsWarn { get; set; } = true;
        public bool IsDebug { get; set; } = true;
        public bool IsTrace { get; set; } = true;
        public bool IsError { get; set; } = true;
    }
}
