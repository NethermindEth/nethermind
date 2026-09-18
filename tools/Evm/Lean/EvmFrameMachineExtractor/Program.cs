// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Evm.Lean.EvmFrameMachineExtractor;

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            Arguments arguments = Arguments.Parse(args);
            ExtractionResult result = EvmFrameMachineProfile.Extract(
                arguments.RepoRoot,
                arguments.OutputDirectory,
                arguments.LeanOutputPath);
            Console.WriteLine($"Extracted {result.OpcodeRouteCount} dispatch-table/byte routes, {result.SourceCount} sources, and {result.AdmissionCount} exact syntax admissions.");
            Console.WriteLine($"IR: {result.IrPath}");
            Console.WriteLine($"Manifest: {result.ManifestPath}");
            Console.WriteLine($"Lean: {result.LeanPath}");
            return 0;
        }
        catch (Exception exception) when (exception is ExtractionException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }
}

internal sealed record Arguments(string RepoRoot, string OutputDirectory, string? LeanOutputPath)
{
    internal static Arguments Parse(string[] args)
    {
        string? repoRoot = null;
        string? outputDirectory = null;
        string? leanOutputPath = null;
        for (int index = 0; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length)
                throw new ArgumentException($"Missing value for argument '{args[index]}'.");

            switch (args[index])
            {
                case "--repo-root" when repoRoot is null:
                    repoRoot = args[index + 1];
                    break;
                case "--output" when outputDirectory is null:
                    outputDirectory = args[index + 1];
                    break;
                case "--lean-output" when leanOutputPath is null:
                    leanOutputPath = args[index + 1];
                    break;
                case "--repo-root":
                case "--output":
                case "--lean-output":
                    throw new ArgumentException($"Argument '{args[index]}' was provided more than once.");
                default:
                    throw new ArgumentException($"Unknown argument '{args[index]}'.");
            }
        }

        if (string.IsNullOrWhiteSpace(repoRoot))
            throw new ArgumentException("Missing required argument '--repo-root'.");
        if (string.IsNullOrWhiteSpace(outputDirectory))
            throw new ArgumentException("Missing required argument '--output'.");

        return new(
            Path.GetFullPath(repoRoot),
            Path.GetFullPath(outputDirectory),
            leanOutputPath is null ? null : Path.GetFullPath(leanOutputPath));
    }
}
