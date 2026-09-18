// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Evm.Lean.ExtendedStackOpcodeExtractor;

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            Dictionary<string, string> options = new(StringComparer.Ordinal);
            for (int index = 0; index < args.Length; index += 2)
            {
                if (index + 1 == args.Length ||
                    args[index] is not ("--repo-root" or "--output" or "--lean-output") ||
                    !options.TryAdd(args[index], args[index + 1]))
                {
                    throw new ArgumentException(
                        "Expected unique --repo-root and --output pairs, with optional --lean-output.");
                }
            }

            if (!options.TryGetValue("--repo-root", out string? repoRoot) ||
                !options.TryGetValue("--output", out string? output))
                throw new ArgumentException("--repo-root and --output are required.");

            options.TryGetValue("--lean-output", out string? leanOutput);
            ExtractionResult result = ExtendedStackOpcodeProfile.Extract(repoRoot, output, leanOutput);
            Console.WriteLine(
                $"Extracted {result.OpcodeCount} opcode(s), {result.SpecializationCount} specialization(s), and {result.AdmissionCount} exact syntax admission(s).");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }
}
