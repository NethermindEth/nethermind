// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Evm.Lean.EnvironmentOpcodeExtractor;

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            Arguments arguments = Arguments.Parse(args);
            ExtractionResult result = Extractor.Extract(arguments.RepoRoot, arguments.Output, arguments.LeanOutput);
            Console.WriteLine($"Validated and extracted {result.OpcodeCount} Amsterdam environment opcodes from {result.SourceCount} source files.");
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

    private sealed record Arguments(string RepoRoot, string Output, string? LeanOutput)
    {
        public static Arguments Parse(string[] args)
        {
            string? repoRoot = null;
            string? output = null;
            string? leanOutput = null;
            for (int i = 0; i < args.Length; i += 2)
            {
                if (i + 1 >= args.Length)
                    throw new ArgumentException($"Missing value for argument '{args[i]}'.");
                string? target = args[i] switch
                {
                    "--repo-root" => repoRoot,
                    "--output" => output,
                    "--lean-output" => leanOutput,
                    _ => throw new ArgumentException($"Unknown argument '{args[i]}'."),
                };
                if (target is not null)
                    throw new ArgumentException($"Argument '{args[i]}' was provided more than once.");
                switch (args[i])
                {
                    case "--repo-root": repoRoot = args[i + 1]; break;
                    case "--output": output = args[i + 1]; break;
                    case "--lean-output": leanOutput = args[i + 1]; break;
                }
            }
            if (string.IsNullOrWhiteSpace(repoRoot))
                throw new ArgumentException("Missing required argument '--repo-root'.");
            if (string.IsNullOrWhiteSpace(output))
                throw new ArgumentException("Missing required argument '--output'.");
            return new(Path.GetFullPath(repoRoot), Path.GetFullPath(output), leanOutput is null ? null : Path.GetFullPath(leanOutput));
        }
    }
}
