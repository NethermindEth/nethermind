// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.CommandLine;

namespace Evm.Formal;

internal static class FormalCommand
{
    public static void Configure(ref RootCommand rootCommand)
    {
        Command formalCommand = new("formal", "Formal differential execution commands");
        Command chargeStateCommand = new("charge-state", "Run state-gas charges from newline-delimited JSON on standard input");
        Command stateGasTransitionCommand = new("state-transition", "Run state-gas transitions from newline-delimited JSON on standard input");
        Option<string> repositoryRootOption = new("--repo-root")
        {
            Description = "Path to the Nethermind repository root.",
        };
        Option<string> outputOption = new("--output")
        {
            Description = "Write the canonical coverage JSON to this file instead of standard output.",
        };
        Command coverageCommand = new("coverage", "Generate the pinned standard-mainnet EVM reachability inventory")
        {
            repositoryRootOption,
            outputOption,
        };

        Option<string> processingRepositoryRootOption = new("--repo-root")
        {
            Description = "Path to the Nethermind repository root.",
        };
        Option<string> processingOutputOption = new("--output")
        {
            Description = "Write the canonical processing coverage JSON to this file instead of standard output.",
        };
        Command processingCoverageCommand = new("processing-coverage", "Generate the pinned standard-mainnet transaction and block processing inventory")
        {
            processingRepositoryRootOption,
            processingOutputOption,
        };

        chargeStateCommand.SetAction(_ => ChargeStateNdjson.Run(Console.In, Console.Out, Console.Error));
        stateGasTransitionCommand.SetAction(_ => StateGasTransitionNdjson.Run(Console.In, Console.Out, Console.Error));
        coverageCommand.SetAction(parseResult =>
        {
            string? repositoryRoot = parseResult.GetValue(repositoryRootOption);
            string? outputPath = parseResult.GetValue(outputOption);
            return CoverageGate.Run(repositoryRoot, outputPath, Console.Out, Console.Error);
        });
        processingCoverageCommand.SetAction(parseResult =>
        {
            string? repositoryRoot = parseResult.GetValue(processingRepositoryRootOption);
            string? outputPath = parseResult.GetValue(processingOutputOption);
            return ProcessingCoverageGate.Run(repositoryRoot, outputPath, Console.Out, Console.Error);
        });
        formalCommand.Add(chargeStateCommand);
        formalCommand.Add(stateGasTransitionCommand);
        formalCommand.Add(coverageCommand);
        formalCommand.Add(processingCoverageCommand);
        rootCommand.Add(formalCommand);
    }
}
