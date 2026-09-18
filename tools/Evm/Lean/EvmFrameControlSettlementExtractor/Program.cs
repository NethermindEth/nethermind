// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Evm.Lean.EvmFrameControlSettlementExtractor;

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            Arguments arguments = Arguments.Parse(args);
            ExtractionResult result = EvmFrameControlSettlementProfile.Extract(
                arguments.RepositoryRoot,
                arguments.OutputDirectory,
                arguments.LeanOutputPath);
            Console.WriteLine($"Extracted {result.BranchCount} shell branches, {result.SourceCount} sources, " +
                $"{result.MemberCount} members, and {result.DependencyCount} explicit theorem adapters.");
            Console.WriteLine($"IR: {result.IrPath} ({result.IrSha256})");
            Console.WriteLine($"Manifest: {result.ManifestPath} ({result.ManifestSha256})");
            Console.WriteLine($"Lean: {result.LeanPath} ({result.LeanSha256})");
            return 0;
        }
        catch (Exception exception) when (exception is ExtractionException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    private sealed record Arguments(string RepositoryRoot, string OutputDirectory, string? LeanOutputPath)
    {
        internal static Arguments Parse(string[] args)
        {
            string? repositoryRoot = null;
            string? outputDirectory = null;
            string? leanOutputPath = null;
            for (int index = 0; index < args.Length; index++)
            {
                switch (args[index])
                {
                    case "--repo-root":
                        repositoryRoot = ReadValue(args, ref index, "--repo-root");
                        break;
                    case "--output":
                        outputDirectory = ReadValue(args, ref index, "--output");
                        break;
                    case "--lean-output":
                        leanOutputPath = ReadValue(args, ref index, "--lean-output");
                        break;
                    default:
                        throw new ArgumentException($"Unknown argument '{args[index]}'.");
                }
            }

            if (string.IsNullOrWhiteSpace(repositoryRoot))
                throw new ArgumentException("Missing required argument '--repo-root'.");
            if (string.IsNullOrWhiteSpace(outputDirectory))
                throw new ArgumentException("Missing required argument '--output'.");

            return new(
                Path.GetFullPath(repositoryRoot),
                Path.GetFullPath(outputDirectory),
                leanOutputPath is null ? null : Path.GetFullPath(leanOutputPath));
        }

        private static string ReadValue(string[] args, ref int index, string option)
        {
            if (++index >= args.Length)
                throw new ArgumentException($"Missing value for argument '{option}'.");
            return args[index];
        }
    }
}
