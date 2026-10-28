// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.CommandLine;
using System.IO.Abstractions;
using Nethermind.Era1;
using Testably.Abstractions;

IFileSystem fileSystem = new RealFileSystem();

Argument<string> pathArgument = new("path")
{
    Description = "An era1 file or a directory with era1 files."
};

Option<string> outputOption = new("--output", "-o")
{
    Description = "The directory to write accumulators.txt and checksums.txt to. Existing files are overwritten.",
    HelpName = "path",
    DefaultValueFactory = r => Environment.CurrentDirectory
};

Argument<string> directoryArgument = new("directory")
{
    Description = "A directory with era1 files, checksums.txt and optional accumulators.txt."
};

Option<string> networkOption = new("--network", "-n")
{
    Description = "The network name that prefixes the era1 file names, for example mainnet.",
    Required = true
};

Command generateCommand = new("generate", "Computes the accumulator and checksum of each era1 file and writes the manifests.")
{
    pathArgument,
    networkOption,
    outputOption
};
generateCommand.SetAction((parseResult, cancellationToken) => RunCommand(async () =>
{
    string output = parseResult.GetValue(outputOption)!;
    await EraManifestGenerator.GenerateAsync(parseResult.GetValue(pathArgument)!, parseResult.GetValue(networkOption)!, output, fileSystem, cancellationToken);
    Console.WriteLine($"Wrote {EraExporter.AccumulatorFileName} and {EraExporter.ChecksumsFileName} to {output}");
    return 0;
}));

Command verifyCommand = new("verify", "Checks that the manifests list the accumulator and checksum of each era1 file in epoch order.")
{
    directoryArgument,
    networkOption
};
verifyCommand.SetAction((parseResult, cancellationToken) => RunCommand(async () =>
{
    string directory = parseResult.GetValue(directoryArgument)!;
    IReadOnlyList<string> mismatches = await EraManifestGenerator.VerifyAsync(
        directory, parseResult.GetValue(networkOption)!, fileSystem, cancellationToken);
    if (!fileSystem.File.Exists(Path.Combine(directory, EraExporter.AccumulatorFileName)))
    {
        Console.WriteLine($"{EraExporter.AccumulatorFileName} not found; skipping the optional accumulator manifest.");
    }

    foreach (string mismatch in mismatches)
    {
        Console.Error.WriteLine(mismatch);
    }

    Console.WriteLine(mismatches.Count == 0 ? "Manifests match" : $"{mismatches.Count} mismatch(es) found");
    return mismatches.Count == 0 ? 0 : 1;
}));

RootCommand rootCommand = new("Generates or verifies the accumulators.txt and checksums.txt manifests of era1 files.")
{
    generateCommand,
    verifyCommand
};

return await rootCommand.Parse(args).InvokeAsync();

static async Task<int> RunCommand(Func<Task<int>> command)
{
    try
    {
        return await command();
    }
    catch (OperationCanceledException)
    {
        Console.Error.WriteLine("Cancelled");
        return 130; // The conventional exit code for termination by Ctrl+C
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine(ex.Message);
        return 1;
    }
}
