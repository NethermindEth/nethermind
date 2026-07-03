// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.CommandLine;
using System.Globalization;
using System.Text;
using Nethermind.StatelessInputGen;
using Spectre.Console;

Console.OutputEncoding = Encoding.UTF8;

Option<bool> noZiskOption = new("--no-zisk")
{
    Description = "Don't wrap the serialized data in Zisk input frame.",
    DefaultValueFactory = r => false
};
Option<string> outputOption = new("--output", "-o")
{
    Description = "The directory in which to place the generated files.",
    HelpName = "path",
    DefaultValueFactory = r => Environment.CurrentDirectory
};

Command rpcCommand = CreateRpcCommand();
Command fixtureCommand = CreateFixtureCommand();
RootCommand rootCommand = [rpcCommand, fixtureCommand];

return await rootCommand.Parse(args).InvokeAsync();

Command CreateRpcCommand()
{
    Option<string> urlOption = new("--url")
    {
        Description = "The JSON-RPC endpoint.",
        HelpName = "url",
        DefaultValueFactory = r => "http://localhost:8545"
    };
    Option<string> blockOption = new("--block", "-b")
    {
        Description = "The block number to generate input from. Also allows `earliest`, `finalized`, `latest`, `pending`, or `safe`.",
        HelpName = "number",
        DefaultValueFactory = r => "latest"
    };

    urlOption.Validators.Add(optionResult =>
    {
        if (!Uri.TryCreate(optionResult.GetValueOrDefault<string>(), UriKind.Absolute, out _))
            optionResult.AddError($"{urlOption.Name} must be a valid absolute URL");
    });

    blockOption.Validators.Add(optionResult =>
    {
        string? value = optionResult.GetValueOrDefault<string>();

        if (string.IsNullOrWhiteSpace(value))
        {
            optionResult.AddError($"{blockOption.Name} must be specified");
        }
        else
        {
            if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                value = value[2..];

            if (!(value is "earliest" or "finalized" or "latest" or "pending" or "safe") &&
                !ulong.TryParse(value, NumberStyles.HexNumber, null, out _))
            {
                optionResult.AddError($"{blockOption.Name} must be a valid block number or either of `earliest`, `finalized`, `latest`, `pending`, or `safe`");
            }
        }
    });

    Command command = new("rpc");
    command.Options.Add(urlOption);
    command.Options.Add(blockOption);
    command.Options.Add(noZiskOption);
    command.Options.Add(outputOption);

    command.SetAction(parseResult => RunCommand(() => InputGenerator.Generate(
        parseResult.GetValue(blockOption)!,
        new Uri(parseResult.GetValue(urlOption)!, UriKind.Absolute),
        parseResult.GetValue(outputOption)!,
        !parseResult.GetValue(noZiskOption)
    )));

    return command;
}

Command CreateFixtureCommand()
{
    Option<string> pathOption = new("--path", "-p")
    {
        Description = "The directory with fixtures or the fixture file.",
        HelpName = "path",
        DefaultValueFactory = r => Environment.CurrentDirectory
    };

    Command command = new("fixture");
    command.Options.Add(pathOption);
    command.Options.Add(noZiskOption);
    command.Options.Add(outputOption);

    command.SetAction((parseResult, cancellationToken) => RunCommand(() => InputExtractor.ExtractFromFixtures(
        parseResult.GetValue(pathOption)!,
        parseResult.GetValue(outputOption)!,
        !parseResult.GetValue(noZiskOption),
        cancellationToken
    )));

    return command;
}

static async Task<int> RunCommand(Func<Task<int>> command)
{
    try
    {
        return await command();
    }
    catch (Exception ex)
    {
        AnsiConsole.MarkupLine($"[red]{ex.Message.EscapeMarkup()}[/]");
        return 1;
    }
}
