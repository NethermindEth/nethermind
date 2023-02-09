// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO.Abstractions;
using System.Linq;

namespace Nethermind.Cli.Console
{
    internal class StatementHistoryManager
    {
        private readonly ICliConsole _cliConsole;
        private readonly IFileSystem _fileSystem;

        private const string HistoryFilePath = "cli.cmd.history";
        private bool _writeFailNotReported = true;

        private static IEnumerable<string> SecuredCommands
        {
            get
            {
                yield return "unlockAccount";
                yield return "newAccount";
            }
        }

        private const string RemovedString = "*removed*";

        public StatementHistoryManager(ICliConsole cliConsole, IFileSystem fileSystem)
        {
            _cliConsole = cliConsole;
            _fileSystem = fileSystem;
        }

        public void UpdateHistory(string statement)
        {
            try
            {
                if (!_fileSystem.File.Exists(HistoryFilePath))
                {
                    _fileSystem.File.Create(HistoryFilePath).Dispose();
                }

                if (!SecuredCommands.Any(statement.Contains))
                {
                    List<string> history = ReadLine.GetHistory();
                    if (history.LastOrDefault() != statement)
                    {
                        ReadLine.AddHistory(statement);
                        _fileSystem.File.AppendAllLines(HistoryFilePath, new[] { statement });
                    }
                }
                else
                {
                    ReadLine.AddHistory(RemovedString);
                }
            }
            catch (Exception e)
            {
                if (_writeFailNotReported)
                {
                    _writeFailNotReported = false;
                    _cliConsole.WriteErrorLine($"Could not write cmd history to {HistoryFilePath} {e.Message}");
                }
            }
        }

        public void Init()
        {
            try
            {
                _cliConsole.WriteInteresting(
                    $"Loading history file from {_fileSystem.Path.Combine(AppDomain.CurrentDomain.BaseDirectory!, HistoryFilePath)}" + Environment.NewLine);

                if (_fileSystem.File.Exists(HistoryFilePath))
                {
                    foreach (string line in _fileSystem.File.ReadLines(HistoryFilePath).TakeLast(60))
                    {
                        ReadLine.AddHistory(line);
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
