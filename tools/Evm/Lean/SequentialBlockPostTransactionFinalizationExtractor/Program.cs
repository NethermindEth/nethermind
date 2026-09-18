// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Evm.Lean.SequentialBlockPostTransactionFinalizationExtractor;

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            Arguments arguments = Arguments.Parse(args);
            if (arguments.Check)
            {
                Extractor.ValidateCheckedIn(arguments.RepoRoot, arguments.OutputDirectory);
                Console.WriteLine("Validated sequential post-transaction finalization source and artifacts.");
                return 0;
            }

            if (arguments.PublicationCheck)
            {
                ProcessOneValidatedPublicationExtractor.ValidateCheckedIn(arguments.RepoRoot, arguments.OutputDirectory);
                Console.WriteLine("Validated ProcessOne publication source and artifacts.");
                return 0;
            }

            if (arguments.PublicationExtract)
            {
                ProcessOnePublicationExtractionResult publication =
                    ProcessOneValidatedPublicationExtractor.Extract(arguments.RepoRoot, arguments.OutputDirectory!);
                Console.WriteLine($"Extracted ProcessOne validated-publication suffix: {publication.AnchorCount} anchors and " +
                    $"{publication.ControlFlowCount} CFGs.");
                Console.WriteLine($"IR: {publication.IrPath}");
                Console.WriteLine($"Manifest: {publication.ManifestPath}");
                Console.WriteLine($"Lean: {publication.LeanPath}");
                return 0;
            }

            ExtractionResult result = Extractor.Extract(arguments.RepoRoot, arguments.OutputDirectory!, arguments.LeanOutputPath);
            Console.WriteLine($"Extracted {result.SourceCount} sources, {result.MemberCount} members, " +
                $"{result.AnchorCount} anchors, and {result.ControlFlowCount} CFGs.");
            Console.WriteLine($"IR: {result.IrPath}");
            Console.WriteLine($"Manifest: {result.ManifestPath}");
            Console.WriteLine($"Lean: {result.LeanPath}");
            return 0;
        }
        catch (Exception exception) when (exception is ArgumentException or ExtractionException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    private sealed record Arguments(
        string RepoRoot,
        string? OutputDirectory,
        string? LeanOutputPath,
        bool Check,
        bool PublicationExtract,
        bool PublicationCheck)
    {
        internal static Arguments Parse(string[] args)
        {
            string? repoRoot = null;
            string? outputDirectory = null;
            string? leanOutputPath = null;
            bool check = false;
            bool publicationExtract = false;
            bool publicationCheck = false;

            int index = 0;
            for (; index < args.Length; index++)
            {
                switch (args[index])
                {
                    case "--check" when !check:
                        check = true;
                        break;
                    case "--check":
                        throw new ArgumentException("Argument '--check' was provided more than once.");
                    case "--repo-root" when repoRoot is null:
                        repoRoot = ReadValue(args[index]);
                        break;
                    case "--repo-root":
                        throw new ArgumentException("Argument '--repo-root' was provided more than once.");
                    case "--output" when outputDirectory is null:
                        outputDirectory = ReadValue(args[index]);
                        break;
                    case "--output":
                        throw new ArgumentException("Argument '--output' was provided more than once.");
                    case "--lean-output" when leanOutputPath is null:
                        leanOutputPath = ReadValue(args[index]);
                        break;
                    case "--lean-output":
                        throw new ArgumentException("Argument '--lean-output' was provided more than once.");
                    case "--publication-extract" when !publicationExtract:
                        publicationExtract = true;
                        break;
                    case "--publication-extract":
                        throw new ArgumentException("Argument '--publication-extract' was provided more than once.");
                    case "--publication-check" when !publicationCheck:
                        publicationCheck = true;
                        break;
                    case "--publication-check":
                        throw new ArgumentException("Argument '--publication-check' was provided more than once.");
                    default:
                        throw new ArgumentException($"Unknown argument '{args[index]}'.");
                }
            }

            if (string.IsNullOrWhiteSpace(repoRoot))
            {
                throw new ArgumentException("Missing required argument '--repo-root'.");
            }

            if ((check ? 1 : 0) + (publicationExtract ? 1 : 0) + (publicationCheck ? 1 : 0) > 1)
            {
                throw new ArgumentException("Use exactly one of '--check', '--publication-check', or '--publication-extract'.");
            }

            if (!check && !publicationCheck && string.IsNullOrWhiteSpace(outputDirectory))
            {
                throw new ArgumentException("Missing required argument '--output'.");
            }

            return new(
                Path.GetFullPath(repoRoot),
                outputDirectory is null ? null : Path.GetFullPath(outputDirectory),
                leanOutputPath is null ? null : Path.GetFullPath(leanOutputPath),
                check,
                publicationExtract,
                publicationCheck);

            string ReadValue(string argument)
            {
                if (++index >= args.Length)
                {
                    throw new ArgumentException($"Missing value for argument '{argument}'.");
                }

                return args[index];
            }
        }
    }
}
