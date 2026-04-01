// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.CommandLine;
using System.Globalization;
using System.Text;
using Nethermind.StatelessInputGen;
using Spectre.Console;

Console.OutputEncoding = Encoding.UTF8;

Option<string> hostOption = new("--rpc-url", "-r")
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

RootCommand rootCommand =
[
    hostOption,
    blockOption,
    noZiskOption,
    outputOption
];
rootCommand.SetAction(async parseResult =>
{
    if (!Uri.TryCreate(parseResult.GetValue(hostOption), UriKind.Absolute, out Uri? host))
        AnsiConsole.MarkupLine("[red]Invalid host URL[/]");

    try
    {
        return await InputGenerator.Generate(
            parseResult.GetValue(blockOption)!,
            host!,
            parseResult.GetValue(outputOption)!,
            !parseResult.GetValue(noZiskOption)
        );
    }
    catch (Exception ex)
    {
        AnsiConsole.MarkupLine($"[red]{ex.Message}[/]");
        return 1;
    }
});

return await rootCommand.Parse(args).InvokeAsync();
