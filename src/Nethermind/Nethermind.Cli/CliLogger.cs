// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Cli.Console;
using Nethermind.Logging;

namespace Nethermind.Cli
{
    internal class CliLogger : ILogger
    {
        private readonly ICliConsole _cliConsole;

        public CliLogger(ICliConsole cliConsole)
        {
            _cliConsole = cliConsole;
        }

        public void Info(string text)
        {
            throw new NotImplementedException();
        }

        public void Warn(string text)
        {
            _cliConsole.WriteLessImportant(text);
        }

        public void Debug(string text)
        {
            throw new NotImplementedException();
        }

        public void Trace(string text)
        {
            throw new NotImplementedException();
        }

        public void Error(string text, Exception? ex = null)
        {
            _cliConsole.WriteErrorLine(text);
            if (ex is not null)
            {
                _cliConsole.WriteException(ex);
            }
        }

        public bool IsInfo => false;
        public bool IsWarn => true;
        public bool IsDebug => false;
        public bool IsTrace => false;
        public bool IsError => true;
    }
}
