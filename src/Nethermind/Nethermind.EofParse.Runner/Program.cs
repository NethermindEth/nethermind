// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.CommandLine;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core.Extensions;
using Nethermind.Evm.EvmObjectFormat;

namespace Nethermind.EofParse.Runner;

internal class Program
{
    public class Options
    {
        public static Option<string> Input { get; } =
            new("--input", "-i") { Description = "Set the raw eof test input file or directory. Either 'input' or 'stdin' is required." };

        public static Option<bool> Stdin { get; } =
            new("--stdin", "-x") { Description = "If stdin is used, the eof runner will read inputs (filenames) from stdin, and continue executing until empty line is read." };
    }

    public static async Task<int> Main(params string[] args)
    {
        RootCommand rootCommand =
        [
            Options.Input,
            Options.Stdin
        ];
        rootCommand.SetAction(Run);

        CommandLineConfiguration configuration = new(rootCommand);

        return await configuration.InvokeAsync(args);
    }

    private static Task Run(ParseResult parseResult, CancellationToken cancellationToken)
    {
        string input = parseResult.GetValue(Options.Input);

        if (parseResult.GetValue(Options.Stdin))
            input = Console.ReadLine();

        while (!string.IsNullOrWhiteSpace(input))
        {
            if (!input.StartsWith('#'))
            {
                input = new string(input.Where(c => char.IsLetterOrDigit(c)).ToArray());

                var bytecode = Bytes.FromHexString(input);
                try
                {
                    var validationResult = EofValidator.IsValidEof(bytecode, ValidationStrategy.ValidateRuntimeMode,
                        out EofContainer? header);
                    if (validationResult)
                    {
                        var sectionCount = header.Value.CodeSections.Length;
                        var subContainerCount = header.Value.ContainerSections?.Length ?? 0;
                        var dataCount = header.Value.DataSection.Length;
                        Console.WriteLine($"OK {sectionCount}/{subContainerCount}/{dataCount}");
                    }
                    else
                    {
                        Console.WriteLine($"err: unknown");
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine($"err: {e.Message}");
                }

                if (!parseResult.GetValue(Options.Stdin))
                    break;
            }

            input = Console.ReadLine();
        }

        return Task.CompletedTask;
    }
}
