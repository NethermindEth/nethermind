// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Evm.Lean.OrdinaryStatefulAdmissionPrefixExtractor;

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            Arguments arguments = Arguments.Parse(args);
            if (arguments.Check)
            {
                Extractor.ValidateExistingArtifacts(arguments.RepoRoot, arguments.OutputDirectory, arguments.LeanOutputPath);
                Console.WriteLine("Checked source-derived IR, manifest, and Lean artifacts.");
            }
            else
            {
                ExtractionResult result = Extractor.Extract(arguments.RepoRoot, arguments.OutputDirectory, arguments.LeanOutputPath);
                Console.WriteLine($"Extracted {result.SourceCount} exact source identities and {result.BranchCount} ordered branches.");
                Console.WriteLine($"IR: {result.IrPath} ({result.IrSha256})");
                Console.WriteLine($"Manifest: {result.ManifestPath} ({result.ManifestSha256})");
                Console.WriteLine($"Lean: {result.LeanPath} ({result.LeanSha256})");
            }

            return 0;
        }
        catch (Exception exception) when (exception is ArgumentException or ExtractionException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    private sealed record Arguments(string RepoRoot, string OutputDirectory, string? LeanOutputPath, bool Check)
    {
        internal static Arguments Parse(string[] args)
        {
            string? repoRoot = null;
            string? output = null;
            string? leanOutput = null;
            bool check = false;

            for (int index = 0; index < args.Length;)
            {
                if (args[index] == "--check")
                {
                    if (check)
                    {
                        throw new ArgumentException("Argument '--check' was provided more than once.");
                    }

                    check = true;
                    index++;
                    continue;
                }

                if (index + 1 >= args.Length)
                {
                    throw new ArgumentException($"Missing value for argument '{args[index]}'.");
                }

                string value = args[index + 1];
                switch (args[index])
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
                        throw new ArgumentException($"Argument '{args[index]}' was provided more than once.");
                    default:
                        throw new ArgumentException($"Unknown argument '{args[index]}'.");
                }

                index += 2;
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
                leanOutput is null ? null : Path.GetFullPath(leanOutput),
                check);
        }
    }
}
