// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using Nethermind.Logging;

namespace Nethermind.Core.Test
{
    public class TestLogger : InterfaceLogger
    {
        private readonly Lock _lock = new();

        public List<string> LogList { get; set; } = [];

        public void Info(string text) => Add(text);

        public void Warn(string text) => Add(text);

        public void Debug(string text) => Add(text);

        public void Trace(string text) => Add(text);

        public void Error(string text, Exception? ex = null) => Add(text);

        // Code under test may log from several threads at once, and concurrent adds can leave null entries behind.
        private void Add(string text)
        {
            lock (_lock)
            {
                LogList.Add(text);
            }
        }

        public bool IsInfo { get; set; } = true;
        public bool IsWarn { get; set; } = true;
        public bool IsDebug { get; set; } = true;
        public bool IsTrace { get; set; } = true;
        public bool IsError { get; set; } = true;
    }
}
