// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.CommandLine;
using System.CommandLine.Help;
using System.Reflection;
using Evm.T8n.JsonTypes;
using Nethermind.Logging;
using Nethermind.Logging.NLog;

namespace Evm.T8n;

public static class T8nCommand
{
    private static readonly ILogManager _logManager = new NLogManager("t8n.log");

    private static CliOption<bool> VersionOpt { get; } = new("-v")
    {
        Description = "Display EVM version"
    };

    public static void Configure(CliRootCommand rootCmd)
    {
        CliCommand t8nCmd = T8nCommandOptions.CreateCommand();

        rootCmd.Add(VersionOpt);
        rootCmd.Add(t8nCmd);

        rootCmd.SetAction(result =>
        {
            if (result.GetValue(VersionOpt))
            {
                Version? version = Assembly.GetExecutingAssembly().GetName().Version;

                Console.WriteLine($"Nethermind EVM version: {version}");
            }
            else
            {
                var helpBuilder = new HelpBuilder();
                helpBuilder.Write(rootCmd, Console.Out);
            }
            Environment.ExitCode = 0;
        });
        t8nCmd.SetAction(parseResult =>
        {
            T8nOutput t8nOutput = T8nTool.Run(T8nCommandArguments.FromParseResult(parseResult), _logManager);
            Environment.ExitCode = t8nOutput.ExitCode;
        });

    }
}
