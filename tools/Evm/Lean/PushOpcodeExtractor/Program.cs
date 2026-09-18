// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Evm.Lean.PushOpcodeExtractor;

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length is < 1 or > 3)
                throw new ArgumentException("Usage: PushOpcodeExtractor <repo-root> [output-directory] [lean-output-path]");
            string root = Path.GetFullPath(args[0]);
            string output = args.Length > 1 ? Path.GetFullPath(args[1]) : Path.Combine(root, PushOpcodeProfile.DefaultOutputRelativePath);
            string? lean = args.Length > 2 ? Path.GetFullPath(args[2]) : null;
            ExtractionResult result = PushOpcodeProfile.Extract(root, output, lean);
            Console.WriteLine($"Extracted {result.OpcodeCount} PUSH opcodes, {result.SpecializationCount} closed roots, and {result.SourceCount} complete sources.");
            return 0;
        }
        catch (Exception exception) when (exception is ExtractionException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }
}
