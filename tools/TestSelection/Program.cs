// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.CommandLine;
using Nethermind.TestSelection.Commands;

RootCommand root = new("History-based test selection for pull requests: EEST tests and NUnit test projects. The journal of " +
    "finished runs is the source of truth; the model is derived from it. Whenever the selection cannot be trusted, every test runs.")
{
    RepositoryOption.Instance,
    HistoryCommands.Collect(),
    HistoryCommands.Inventory(),
    HistoryCommands.Model(),
    HistoryCommands.Replay(),
    ModelCommands.Download(),
    ModelCommands.Prune(),
    ModelCommands.ReportMasterFailure(),
    SelectProjectsCommand.Create(),
    SelectEestCommand.Create(),
};

return await root.Parse(args).InvokeAsync();
