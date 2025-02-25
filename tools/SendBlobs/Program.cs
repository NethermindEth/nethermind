// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using SendBlobs;
using System.CommandLine;

CliRootCommand rootCommand = [];

SetupCli.SetupExecute(rootCommand);
SetupCli.SetupDistributeCommand(rootCommand);
SetupCli.SetupReclaimCommand(rootCommand);
SetupCli.SetupSendFileCommand(rootCommand);

CliConfiguration cli = new(rootCommand);

return await cli.InvokeAsync(args);
