// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace SendBlobs;

using System.CommandLine;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        CliRootCommand rootCommand = [];

        SetupCli.SetupExecute(rootCommand);
        SetupCli.SetupDistributeCommand(rootCommand);
        SetupCli.SetupReclaimCommand(rootCommand);
        SetupCli.SetupSendFileCommand(rootCommand);

        CliConfiguration cli = new(rootCommand);

        return await cli.InvokeAsync(args);
    }
}

