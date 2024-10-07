// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.ComponentModel;
using Nethermind.DocGen;
using Spectre.Console;
using Spectre.Console.Cli;

var app = new CommandApp<AppCommand>();

app.Run(args);

public sealed class AppCommand : Command<AppSettings>
{
    public override int Execute(CommandContext context, AppSettings settings)
    {
        if (settings.DocsPath is null)
        {
            AnsiConsole.MarkupLine("[red]The path to the docs is not specified[/]");
            return 1;
        }

        if (!Directory.Exists(settings.DocsPath))
        {
            AnsiConsole.MarkupLine("[red]No docs not found at the path specified[/]");
            return 1;
        }

        if (settings.GenerateConfig)
            ConfigGenerator.Generate(settings.DocsPath);

        if (settings.GenerateDBSize)
            DBSizeGenerator.Generate(settings.DocsPath);

        if (settings.GenerateJsonRpc)
            JsonRpcGenerator.Generate(settings.DocsPath);

        if (settings.GenerateMetrics)
            MetricsGenerator.Generate(settings.DocsPath);

        return 0;
    }
}

public sealed class AppSettings : CommandSettings
{
    [Description("Path to the docs")]
    [CommandArgument(0, "[docspath]")]
    public string? DocsPath { get; init; }

    [CommandOption("--config")]
    public bool GenerateConfig { get; init; }

    [CommandOption("--dbsize")]
    public bool GenerateDBSize { get; init; }

    [CommandOption("--jsonrpc")]
    public bool GenerateJsonRpc { get; init; }

    [CommandOption("--metrics")]
    public bool GenerateMetrics { get; init; }
}
