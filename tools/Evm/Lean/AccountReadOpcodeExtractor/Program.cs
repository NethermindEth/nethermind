// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Evm.Lean.AccountReadOpcodeExtractor;

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            string repoRoot = Directory.GetCurrentDirectory();
            string output = Path.Combine(repoRoot, AccountReadOpcodeProfile.DefaultOutputRelativePath);
            string? lean = null;
            for (int index = 0; index < args.Length; index++)
            {
                switch (args[index])
                {
                    case "--repo-root" when index + 1 < args.Length:
                        repoRoot = args[++index];
                        break;
                    case "--output" when index + 1 < args.Length:
                        output = args[++index];
                        break;
                    case "--lean-output" when index + 1 < args.Length:
                        lean = args[++index];
                        break;
                    default:
                        throw new ExtractionException($"Unknown or incomplete argument: {args[index]}");
                }
            }

            ExtractionResult result = AccountReadOpcodeProfile.Extract(repoRoot, output, lean);
            Console.WriteLine($"Extracted {result.OpcodeCount} account-read opcodes, {result.SpecializationCount} closed roots, " +
                $"and {result.AdmissionCount} source admissions from {result.SourceCount} files.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }
}
