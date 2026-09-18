// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Evm.Lean.PrecompileFrameExtractor;

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            Arguments arguments = Arguments.Parse(args);
            ExtractionResult result = PrecompileFrameProfile.Extract(
                arguments.RepoRoot,
                arguments.OutputDirectory,
                arguments.LeanOutputPath,
                arguments.EnableZkEvm);
            Console.WriteLine($"Validated {result.RouteCount} precompile routes, {result.SourceCount} sources, " +
                $"{result.MemberCount} members, and {result.DependencyCount} dependencies.");
            Console.WriteLine($"IR: {result.IrPath} ({result.IrSha256})");
            Console.WriteLine($"Manifest: {result.ManifestPath} ({result.ManifestSha256})");
            Console.WriteLine($"Lean: {result.LeanPath} ({result.LeanSha256})");
            Console.WriteLine($"Stage B: {result.StageB.BranchCount} operational branches and " +
                $"{result.StageB.OracleCount} typed leaf oracles.");
            Console.WriteLine($"Stage B IR: {result.StageB.IrPath} ({result.StageB.IrSha256})");
            Console.WriteLine($"Stage B manifest: {result.StageB.ManifestPath} ({result.StageB.ManifestSha256})");
            Console.WriteLine($"Stage B Lean: {result.StageB.LeanPath} ({result.StageB.LeanSha256})");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    private sealed record Arguments(
        string RepoRoot,
        string OutputDirectory,
        string? LeanOutputPath,
        bool EnableZkEvm)
    {
        internal static Arguments Parse(string[] args)
        {
            string? repoRoot = null;
            string? output = null;
            string? leanOutput = null;
            bool enableZkEvm = false;

            for (int index = 0; index < args.Length; index++)
            {
                switch (args[index])
                {
                    case "--repo-root":
                        repoRoot = ReadValue(args, ref index, "--repo-root");
                        break;
                    case "--output":
                        output = ReadValue(args, ref index, "--output");
                        break;
                    case "--lean-output":
                        leanOutput = ReadValue(args, ref index, "--lean-output");
                        break;
                    case "--enable-zkevm":
                        if (enableZkEvm)
                        {
                            throw new ArgumentException("Argument '--enable-zkevm' was provided more than once.");
                        }

                        enableZkEvm = true;
                        break;
                    default:
                        throw new ArgumentException($"Unknown argument '{args[index]}'.");
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

            return new(
                Path.GetFullPath(repoRoot),
                Path.GetFullPath(output),
                leanOutput is null ? null : Path.GetFullPath(leanOutput),
                enableZkEvm);
        }

        private static string ReadValue(string[] args, ref int index, string option)
        {
            if (index + 1 >= args.Length)
            {
                throw new ArgumentException($"Missing value for argument '{option}'.");
            }

            index++;
            return args[index];
        }
    }
}
