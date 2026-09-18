// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Evm.Lean.PersistentStorageOpcodeExtractor;

internal static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            if (args.Length is < 1 or > 3)
            {
                Console.Error.WriteLine("Usage: PersistentStorageOpcodeExtractor <repo-root> [output-directory] [lean-output-path]");
                return 2;
            }

            string repoRoot = Path.GetFullPath(args[0]);
            string output = args.Length >= 2
                ? Path.GetFullPath(args[1])
                : Path.Combine(repoRoot, PersistentStorageOpcodeProfile.DefaultOutputRelativePath);
            string? lean = args.Length == 3 ? Path.GetFullPath(args[2]) : null;
            ExtractionResult result = PersistentStorageOpcodeProfile.Extract(repoRoot, output, lean);
            Console.WriteLine($"Extracted {result.SourceCount} sources and {result.SpecializationCount} closed dispatch specializations.");
            Console.WriteLine(result.IrPath);
            Console.WriteLine(result.ManifestPath);
            Console.WriteLine(result.LeanPath);
            return 0;
        }
        catch (Exception exception) when (exception is ExtractionException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }
}
