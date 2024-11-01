// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.CommandLine;

namespace Evm.t8n;

public static class T8NCommand
{
    public static void Configure(ref CliRootCommand rootCmd)
    {
        CliCommand cmd = T8NCommandOptions.CreateCommand();

        cmd.SetAction(parseResult =>
        {
            var arguments = T8NCommandArguments.FromParseResult(parseResult);

            T8NExecutor.Execute(arguments);
        });

        rootCmd.Add(cmd);
    }
}
