// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Evm.Lean.EvmFrameDriverExtractor;

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            Arguments arguments = Arguments.Parse(args);
            if (arguments.Stage.Equals("operational", StringComparison.OrdinalIgnoreCase))
            {
                OperationalExtractionResult result = OperationalProfile.Extract(
                    arguments.RepositoryRoot, arguments.OutputDirectory, arguments.LeanOutputPath);
                Console.WriteLine("Extracted Stage F operational loop with " + result.SourceCount +
                    " sources, " + result.MemberCount + " members, " + result.DependencyCount +
                    " accepted dependencies, " + result.AdapterCount + " adapters, and " +
                    result.MutationVectorCount + " mutation vectors.");
                Console.WriteLine("IR: " + result.IrPath + " (" + result.IrSha256 + ")");
                Console.WriteLine("Manifest: " + result.ManifestPath + " (" + result.ManifestSha256 + ")");
                Console.WriteLine("Lean: " + result.LeanPath + " (" + result.LeanSha256 + ")");
            }
            else
            {
                ExtractionResult result = EvmFrameDriverProfile.Extract(
                    arguments.RepositoryRoot, arguments.OutputDirectory, arguments.LeanOutputPath);
                Console.WriteLine("Extracted " + result.BranchCount + " branches, " +
                    result.SourceCount + " sources, " + result.MemberCount + " members, " +
                    result.DependencyCount + " dependencies, and " + result.ControlAnchorCount + " Roslyn control anchors.");
                Console.WriteLine("IR: " + result.IrPath + " (" + result.IrSha256 + ")");
                Console.WriteLine("Manifest: " + result.ManifestPath + " (" + result.ManifestSha256 + ")");
                Console.WriteLine("Lean: " + result.LeanPath + " (" + result.LeanSha256 + ")");
            }
            return 0;
        }
        catch (Exception exception) when (exception is ExtractionException or ArgumentException or IOException ||
                                            exception is UnauthorizedAccessException)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    private sealed record Arguments(string RepositoryRoot, string OutputDirectory, string? LeanOutputPath, string Stage)
    {
        internal static Arguments Parse(string[] args)
        {
            string? repositoryRoot = null;
            string? outputDirectory = null;
            string? leanOutputPath = null;
            string stage = "stage-e";
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
                    case "--stage":
                        stage = ReadValue(args, ref index, "--stage");
                        break;
                    default:
                        throw new ArgumentException("Unknown argument '" + args[index] + "'.");
                }
            }
            if (string.IsNullOrWhiteSpace(repositoryRoot))
                throw new ArgumentException("Missing required argument '--repo-root'.");
            if (string.IsNullOrWhiteSpace(outputDirectory))
                throw new ArgumentException("Missing required argument '--output'.");
            if (!stage.Equals("stage-e", StringComparison.OrdinalIgnoreCase) &&
                !stage.Equals("operational", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Stage must be 'stage-e' or 'operational'.");
            return new(Path.GetFullPath(repositoryRoot), Path.GetFullPath(outputDirectory),
                leanOutputPath is null ? null : Path.GetFullPath(leanOutputPath), stage);
        }

        private static string ReadValue(string[] args, ref int index, string option)
        {
            if (++index >= args.Length)
                throw new ArgumentException("Missing value for argument '" + option + "'.");
            return args[index];
        }
    }
}
