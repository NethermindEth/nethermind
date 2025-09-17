// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.CommandLine;
using Nethermind.DocGen;
using Spectre.Console;

Option<bool> configOption = new("--config") { Description = "Generate configuration options docs" };
Option<bool> dbSizeOption = new("--dbsize") { Description = "Generate DB sizes" };
Option<string> dbSizeSourceOption = new("--dbsize-src")
{
    Description = "The path to the directory with DB size files",
    HelpName = "path"
};
Argument<string> docsDirArg = new("docs-dir")
{
    Description = "The path to the docs directory",
    HelpName = "path"
};
Option<bool> jsonRpcOption = new("--jsonrpc") { Description = "Generate JSON-RPC API docs" };
Option<bool> metricsOption = new("--metrics") { Description = "Generate metrics options docs" };

dbSizeOption.Validators.Add(optionResult =>
{
    if (optionResult.Parent?.GetValue(dbSizeSourceOption) is null)
        optionResult.AddError($"{dbSizeSourceOption.Name} must be specified when {dbSizeOption.Name} is set");
});

RootCommand rootCommand =
[
    configOption,
    dbSizeOption,
    dbSizeSourceOption,
    docsDirArg,
    jsonRpcOption,
    metricsOption
];
rootCommand.SetAction(parseResult =>
{
    var docsPath = parseResult.GetValue(docsDirArg)!;

    if (!Directory.Exists(docsPath))
    {
        AnsiConsole.MarkupLine("[red]The specified docs directory not found[/]");
        return 1;
    }

    if (parseResult.GetValue(configOption))
        ConfigGenerator.Generate(docsPath);

    if (parseResult.GetValue(dbSizeOption))
        DBSizeGenerator.Generate(docsPath, parseResult.GetValue(dbSizeSourceOption));

    if (parseResult.GetValue(jsonRpcOption))
        JsonRpcGenerator.Generate(docsPath);

    if (parseResult.GetValue(metricsOption))
        MetricsGenerator.Generate(docsPath);

    return 0;
});

CommandLineConfiguration cli = new(rootCommand);

return cli.Invoke(args);
