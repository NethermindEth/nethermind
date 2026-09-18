// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Microsoft.CodeAnalysis.FlowAnalysis;

namespace Nethermind.Evm.Lean.SequentialBlockPostTransactionFinalizationExtractor;

internal static partial class Extractor
{
    internal static Dictionary<int, HashSet<int>> ComputeEntryDominators(int entry, int[] blocks, string[] edges)
    {
        Dictionary<int, HashSet<int>> successors = blocks.ToDictionary(static block => block, static _ => new HashSet<int>());
        foreach (string edge in edges)
        {
            if (!TryParseEdge(edge, out int from, out int to) || !successors.ContainsKey(from) || !successors.ContainsKey(to))
                throw new ExtractionException("The normal-flow dominance graph contains an invalid edge.");
            successors[from].Add(to);
        }
        if (!successors.ContainsKey(entry)) throw new ExtractionException("The normal-flow dominance entry is missing.");

        // Exception handlers are independently audited roots, not alternative entries to a
        // normal-return execution. Only actual ordinary paths from this entry affect dominance.
        HashSet<int> reachable = [];
        Queue<int> pending = new();
        pending.Enqueue(entry);
        while (pending.TryDequeue(out int block))
        {
            if (!reachable.Add(block)) continue;
            foreach (int successor in successors[block]) pending.Enqueue(successor);
        }
        Dictionary<int, HashSet<int>> predecessors = reachable.ToDictionary(static block => block, static _ => new HashSet<int>());
        foreach (int block in reachable)
            foreach (int successor in successors[block]) predecessors[successor].Add(block);

        Dictionary<int, HashSet<int>> dominators = reachable.ToDictionary(static block => block,
            block => block == entry ? (HashSet<int>)[entry] : [.. reachable]);
        bool changed;
        do
        {
            changed = false;
            foreach (int block in reachable.Order())
            {
                if (block == entry) continue;
                HashSet<int> next = [.. reachable];
                foreach (int predecessor in predecessors[block]) next.IntersectWith(dominators[predecessor]);
                next.Add(block);
                if (next.SetEquals(dominators[block])) continue;
                dominators[block] = next;
                changed = true;
            }
        }
        while (changed);
        return dominators;
    }

    private static int[] ReadExceptionHandlerEntries(ControlFlowGraph graph) =>
        Regions(graph.Root).Where(static region => region.Kind is ControlFlowRegionKind.Catch or ControlFlowRegionKind.Filter)
            .Select(static region => region.FirstBlockOrdinal)
            .Where(ordinal => graph.Blocks[ordinal].IsReachable).Distinct().Order().ToArray();

    private static IEnumerable<ControlFlowRegion> Regions(ControlFlowRegion region)
    {
        yield return region;
        foreach (ControlFlowRegion child in region.NestedRegions)
            foreach (ControlFlowRegion descendant in Regions(child)) yield return descendant;
    }

    private static ControlFlowTransferIdentity[] ReadTransfers(ControlFlowGraph graph) =>
        graph.Blocks.Where(static block => block.IsReachable).SelectMany(block => Branches(block)
            .Select(branch => new ControlFlowTransferIdentity(block.Ordinal, branch.Destination?.Ordinal ?? -1,
                branch.Semantics.ToString(),
                branch.FinallyRegions.Select(static region => region.FirstBlockOrdinal).ToArray(),
                branch.FinallyRegions.Select(region => graph.Blocks
                    .Where(candidate => candidate.IsReachable && candidate.Ordinal >= region.FirstBlockOrdinal &&
                        candidate.Ordinal <= region.LastBlockOrdinal && Branches(candidate).Any(static exit =>
                            exit.Destination is null && exit.Semantics == ControlFlowBranchSemantics.StructuredExceptionHandling))
                    .Select(static candidate => candidate.Ordinal).ToArray()).ToArray())))
            .ToArray();

    private static string[] TransferEdges(ControlFlowTransferIdentity[] transfers)
    {
        HashSet<string> edges = new(StringComparer.Ordinal);
        foreach (ControlFlowTransferIdentity transfer in transfers)
        {
            int destination = transfer.FinallyEntries.Length > 0 ? transfer.FinallyEntries[0] : transfer.Destination;
            if (destination >= 0) edges.Add($"{transfer.Source}->{destination}");
            for (int index = 0; index < transfer.FinallyEntries.Length; index++)
            {
                int continuation = index + 1 < transfer.FinallyEntries.Length
                    ? transfer.FinallyEntries[index + 1] : transfer.Destination;
                if (continuation < 0) continue;
                foreach (int exit in transfer.FinallyExits[index]) edges.Add($"{exit}->{continuation}");
            }
        }
        return edges.OrderBy(static edge => edge, StringComparer.Ordinal).ToArray();
    }

    private static IEnumerable<BasicBlock> Successors(ControlFlowGraph graph, BasicBlock block)
    {
        foreach (string edge in TransferEdges(ReadTransfers(graph)))
        {
            TryParseEdge(edge, out int source, out int destination);
            if (source == block.Ordinal) yield return graph.Blocks[destination];
        }
    }

    private static void ValidateTransfers(ControlFlowTransferIdentity[] transfers, int[] reachable, string[] edges)
    {
        if (transfers is null || transfers.Any(transfer => transfer is null ||
                !reachable.Contains(transfer.Source) ||
                transfer.Destination < -1 || transfer.Destination >= 0 && !reachable.Contains(transfer.Destination) ||
                transfer.FinallyEntries is null || transfer.FinallyExits is null ||
                transfer.FinallyEntries.Length != transfer.FinallyExits.Length ||
                transfer.FinallyEntries.Any(entry => !reachable.Contains(entry)) ||
                transfer.FinallyExits.Any(exits => exits is null || exits.Any(exit => !reachable.Contains(exit))) ||
                transfer.Semantics is not ("Regular" or "Return" or "Throw" or "Rethrow" or "ProgramTermination" or "StructuredExceptionHandling") ||
                transfer.Destination == -1 && transfer.Semantics is not ("Throw" or "Rethrow" or "ProgramTermination" or "StructuredExceptionHandling")) ||
            !edges.SequenceEqual(TransferEdges(transfers), StringComparer.Ordinal))
            throw new ExtractionException("Finalization CFG transfer/finally topology is incomplete or inconsistent.");
    }
}
