// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Evm.Lean.BlockProcessorExtractor;

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            string? branchMode = args.FirstOrDefault() is "--branch-extract" or "--branch-check" or
                "--finite-extract" or "--finite-check" or "--publication-extract" or "--publication-check" ? args[0] : null;
            if (branchMode is not null) args = args[1..];
            Dictionary<string, string> options = new(StringComparer.Ordinal);
            for (int i = 0; i < args.Length; i += 2)
            {
                if (i + 1 == args.Length || args[i] is not ("--repo-root" or "--output") ||
                    !options.TryAdd(args[i], args[i + 1]))
                    throw new ArgumentException("Expected unique --repo-root and --output pairs.");
            }
            if (options.Count != 2) throw new ArgumentException("--repo-root and --output are required.");
            if (branchMode == "--branch-extract")
                BranchAcceptedIterationExtractor.Extract(options["--repo-root"], options["--output"]);
            else if (branchMode == "--branch-check")
                BranchAcceptedIterationExtractor.Check(options["--repo-root"], options["--output"]);
            else if (branchMode is "--finite-extract" or "--publication-extract")
                OuterBlockExtractor.Extract(options["--repo-root"], options["--output"], branchMode == "--finite-extract" ? OuterBlockExtractor.Finite : OuterBlockExtractor.Publication);
            else if (branchMode is "--finite-check" or "--publication-check")
                OuterBlockExtractor.Check(options["--repo-root"], options["--output"], branchMode == "--finite-check" ? OuterBlockExtractor.Finite : OuterBlockExtractor.Publication);
            else
                Extractor.Extract(options["--repo-root"], options["--output"]);
            Console.WriteLine(branchMode is null
                ? "Extracted admitted block/branch source events and guards; external semantics remain obligations."
                : branchMode is "--branch-extract" or "--branch-check"
                    ? "Processed the separate source-attached branch slice; external hook behavior remains an adapter obligation."
                    : "Processed the separate source-attached static slice; external hook behavior remains an adapter obligation.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }
}
