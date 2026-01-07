// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using StatelessExecution;
using System.CommandLine;

RootCommand rootCommand = [];

SetupCli.SetupExecute(rootCommand);

return await rootCommand.Parse(args).InvokeAsync();
