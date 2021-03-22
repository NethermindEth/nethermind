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
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Nethermind.Cli.Console
{
    internal class StatementHistoryManager
    {
        private readonly ICliConsole _cliConsole;
        private const string HistoryFilePath = "cli.cmd.history";
        private bool writeFailNotReported = true;

        private List<string> _historyCloned = new List<string>();

        private static IEnumerable<string> SecuredCommands
        {
            get
            {
                yield return "unlockAccount";
                yield return "newAccount";
            }
        }

        private const string _removedString = "*removed*";

        public StatementHistoryManager(ICliConsole cliConsole)
        {
            _cliConsole = cliConsole;
        }

        public void UpdateHistory(string statement)
        {
            try
            {
                if (!File.Exists(HistoryFilePath))
                {
                    File.Create(HistoryFilePath).Dispose();
                }

                if (!SecuredCommands.Any(statement.Contains))
                {
                    List<string> history = ReadLine.GetHistory();
                    if (history.LastOrDefault() != statement)
                    {
                        ReadLine.AddHistory(statement);
                        _historyCloned.Insert(0, statement);
                    }
                }
                else
                {
                    ReadLine.AddHistory(_removedString);
                    _historyCloned.Insert(0, statement);
                }

                File.WriteAllLines(HistoryFilePath, _historyCloned.Distinct().Reverse().ToArray());
            }
            catch (Exception e)
            {
                if (writeFailNotReported)
                {
                    writeFailNotReported = false;
                    _cliConsole.WriteErrorLine($"Could not write cmd history to {HistoryFilePath} {e.Message}");
                }
            }
        }

        public void Init()
        {
            try
            {
                _cliConsole.WriteInteresting(
                    $"Loading history file from {Path.Combine(AppDomain.CurrentDomain.BaseDirectory!, HistoryFilePath)}" + Environment.NewLine);

                if (File.Exists(HistoryFilePath))
                {
                    foreach (string line in File.ReadLines(HistoryFilePath).Distinct().TakeLast(60))
                    {
                        if (line != _removedString)
                        {
                            ReadLine.AddHistory(line);
                            _historyCloned.Insert(0, line);
                        }
                    }
                }
            }
            catch (Exception e)
            {
                _cliConsole.WriteErrorLine($"Could not load cmd history from {HistoryFilePath} {e.Message}");
            }
        }
    }
}
