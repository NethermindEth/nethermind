//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

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
            if (ex != null)
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
