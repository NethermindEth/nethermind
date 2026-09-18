// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Evm.Lean.SystemTransactionRoutingExtractor;

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            Arguments arguments = Arguments.Parse(args);
            ExtractionResult result = Extractor.Extract(
                arguments.RepoRoot,
                arguments.OutputDirectory,
                arguments.LeanOutputPath);
            Console.WriteLine($"Validated and extracted {result.SourceCount} exact current source files.");
            Console.WriteLine($"IR: {result.IrPath}");
            Console.WriteLine($"Manifest: {result.ManifestPath}");
            Console.WriteLine($"Lean: {result.LeanPath}");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    private sealed record Arguments(string RepoRoot, string OutputDirectory, string? LeanOutputPath)
    {
        public static Arguments Parse(string[] args)
        {
            string? repoRoot = null;
            string? output = null;
            string? leanOutput = null;

            for (int i = 0; i < args.Length; i += 2)
            {
                if (i + 1 >= args.Length)
                {
                    throw new ArgumentException($"Missing value for argument '{args[i]}'.");
                }

                string value = args[i + 1];
                switch (args[i])
                {
                    case "--repo-root" when repoRoot is null:
                        repoRoot = value;
                        break;
                    case "--output" when output is null:
                        output = value;
                        break;
                    case "--lean-output" when leanOutput is null:
                        leanOutput = value;
                        break;
                    case "--repo-root":
                    case "--output":
                    case "--lean-output":
                        throw new ArgumentException($"Argument '{args[i]}' was provided more than once.");
                    default:
                        throw new ArgumentException($"Unknown argument '{args[i]}'.");
                }
            }

            if (string.IsNullOrWhiteSpace(repoRoot))
            {
                throw new ArgumentException("Missing required argument '--repo-root'.");
            }

            if (string.IsNullOrWhiteSpace(output))
            {
                throw new ArgumentException("Missing required argument '--output'.");
            }

            return new Arguments(
                Path.GetFullPath(repoRoot),
                Path.GetFullPath(output),
                leanOutput is null ? null : Path.GetFullPath(leanOutput));
        }
    }
}
