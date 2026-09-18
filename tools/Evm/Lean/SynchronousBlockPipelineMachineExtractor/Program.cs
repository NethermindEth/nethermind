// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Evm.Lean.SynchronousBlockPipelineMachineExtractor;

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 3 && args[0] == "--audit-pins" && args[1] == "--repo-root")
            {
                AuditResult result = SourceAudit.Run(args[2]);
                Console.WriteLine($"Audited {result.SourceCount} pinned sources, {result.SymbolCount} symbols, {result.AnchorCount} source anchors/edges.");
                Console.WriteLine($"Route phases: {result.PhaseCount}; hook contracts: {result.HookCount}; source audit closed: {result.ExtractionClosed}; operational extraction remains refused.");
                Console.WriteLine("No operational Lean kernel or refinement theorem was emitted.");
                return 0;
            }

            if (args.Length == 5 && args[0] == "--extract" && args[1] == "--repo-root" && args[3] == "--output")
            {
                Extractor.Extract(args[2], args[4]);
                return 1;
            }

            throw new ArgumentException(
                "Usage: --audit-pins --repo-root <path> | --extract --repo-root <path> --output <path>.");
        }
        catch (Exception exception) when (exception is ArgumentException or ExtractionException or IOException
            or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }
}
