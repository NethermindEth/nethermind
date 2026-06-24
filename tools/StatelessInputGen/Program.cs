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
Option<string?> fixtureOption = new("--from-fixture", "-f")
{
    Description = "Path to a StatelessValidationFixture JSON (as produced by zkevm-benchmark-test/witness-generator-cli). " +
                  "When set, the RPC fetch is skipped and the input is built directly from the fixture. " +
                  "Mutually exclusive with --rpc-url / --block.",
    HelpName = "path",
    DefaultValueFactory = r => null
};
Option<bool> chainConfigEnvelopeOption = new("--chain-config-envelope")
{
    Description = "Append a trailing `{\"config\": <chain_config>}` JSON section after the witness, " +
                  "so the patched Nethermind guest can build a SpecProvider from it and validate blocks " +
                  "on chains it doesn't have hardcoded support for. Only meaningful with --from-fixture.",
    DefaultValueFactory = r => false
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
    outputOption,
    fixtureOption,
    chainConfigEnvelopeOption
];
rootCommand.SetAction(async parseResult =>
{
    string? fixturePath = parseResult.GetValue(fixtureOption);
    bool forZisk = !parseResult.GetValue(noZiskOption);
    bool chainConfigEnvelope = parseResult.GetValue(chainConfigEnvelopeOption);
    string output = parseResult.GetValue(outputOption)!;

    try
    {
        if (!string.IsNullOrEmpty(fixturePath))
            return InputGenerator.GenerateFromFixture(fixturePath, output, forZisk, chainConfigEnvelope);

        if (chainConfigEnvelope)
        {
            AnsiConsole.MarkupLine(
                $"[red]{chainConfigEnvelopeOption.Name} requires {fixtureOption.Name}; " +
                "the RPC path does not provide a chain_config section.[/]");
            return 1;
        }

        if (!Uri.TryCreate(parseResult.GetValue(hostOption), UriKind.Absolute, out Uri? host))
        {
            AnsiConsole.MarkupLine("[red]Invalid host URL[/]");
            return 1;
        }

        return await InputGenerator.Generate(
            parseResult.GetValue(blockOption)!,
            host,
            output,
            forZisk
        );
    }
    catch (Exception ex)
    {
        AnsiConsole.MarkupLine($"[red]{ex.Message}[/]");
        return 1;
    }
});

return await rootCommand.Parse(args).InvokeAsync();
