// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.CommandLine;

namespace Evm.T8n;

public static class T8nCommand
{
    public static void Configure(ref CliRootCommand rootCmd)
    {
        CliCommand cmd = T8nCommandOptions.CreateCommand();

        cmd.SetAction(parseResult =>
        {
            var arguments = T8nCommandArguments.FromParseResult(parseResult);

            T8nExecutor.Execute(arguments);
        });

        rootCmd.Add(cmd);
    }
}
