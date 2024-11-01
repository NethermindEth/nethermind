// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.CommandLine;

namespace Evm.t8n;

public static class T8NCommand
{
    public static void Configure(ref RootCommand rootCmd)
    {
        Command cmd = T8NCommandOptions.CreateCommand();
        rootCmd.Add(cmd);

        cmd.SetHandler(
            context =>
            {
                var arguments = T8NCommandArguments.FromParseResult(context.ParseResult);
                T8NExecutor.Execute(arguments);
            });
    }
}
