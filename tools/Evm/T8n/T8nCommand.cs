// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.CommandLine;
using Evm.T8n.JsonTypes;
using Nethermind.Logging;
using Nethermind.Logging.NLog;

namespace Evm.T8n;

public static class T8nCommand
{
    private static ILogManager _logManager = new NLogManager("t8n.log");

    public static void Configure(ref CliRootCommand rootCmd)
    {
        CliCommand cmd = T8nCommandOptions.CreateCommand();

        cmd.SetAction(parseResult =>
        {
            T8nOutput t8nOutput = T8nTool.Run(T8nCommandArguments.FromParseResult(parseResult), _logManager);
            Environment.ExitCode = t8nOutput.ExitCode;
        });

        rootCmd.Add(cmd);
    }
}
