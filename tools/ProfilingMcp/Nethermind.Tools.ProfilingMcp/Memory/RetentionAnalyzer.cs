// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Diagnostics.Runtime;

namespace Nethermind.Tools.ProfilingMcp.Memory;

/// <summary>One link of a retention path: an object and the field of its predecessor that references it.</summary>
public sealed record PathStep(string TypeName, ulong Address, string? Field);

/// <summary>A chain of references from a GC root to an instance of the analyzed type.</summary>
/// <param name="Instances">Number of target instances found behind a path with the same sequence of types and fields.</param>
public sealed record RetentionPath(string RootKind, IReadOnlyList<PathStep> Steps, int Instances);

/// <param name="Referrers">Count of references to target instances, by referencing type ("[root: kind]" for direct GC roots).</param>
/// <param name="SearchComplete">False when the visit limit stopped the root search before every reachable object was seen.</param>
public sealed record RetentionReport(
    IReadOnlyList<string> MatchedTypes,
    long InstanceCount,
    long InstanceBytes,
    IReadOnlyList<KeyValuePair<string, long>> Referrers,
    IReadOnlyList<RetentionPath> Paths,
    bool SearchComplete,
    string Timings = "");

/// <summary>
/// Explains why instances of a type stay alive: which types reference them and the shortest reference chains from GC roots.
/// </summary>
internal static class RetentionAnalyzer
{
    public static RetentionReport Analyze(ClrRuntime runtime, Regex typePattern, int maxPaths, long maxVisited)
    {
        ClrHeap heap = runtime.Heap;
        Stopwatch phase = Stopwatch.StartNew();
        HashSet<ulong> targets = [];
        HashSet<string> matchedTypes = [];
        long targetBytes = 0;
        foreach (ClrObject obj in heap.EnumerateObjects())
        {
            if (obj.IsFree || obj.Type?.Name is not { } name || !typePattern.IsMatch(name))
                continue;

            targets.Add(obj.Address);
            matchedTypes.Add(name);
            targetBytes += (long)obj.Size;
        }

        if (targets.Count == 0)
            return new RetentionReport([], 0, 0, [], [], SearchComplete: true);

        TimeSpan scan = phase.Elapsed;
        List<KeyValuePair<string, long>> referrers = CountReferrers(heap, targets);
        TimeSpan referrerScan = phase.Elapsed - scan;
        Dictionary<ulong, string> statics = MapStaticFields(runtime);
        TimeSpan staticsScan = phase.Elapsed - scan - referrerScan;
        List<RetentionPath> paths = FindPaths(heap, targets, statics, maxPaths, maxVisited, out bool complete);
        TimeSpan pathSearch = phase.Elapsed - scan - referrerScan - staticsScan;

        return new RetentionReport(
            [.. matchedTypes.Order()],
            targets.Count,
            targetBytes,
            referrers,
            paths,
            complete,
            $"find instances {scan.TotalSeconds:F1} s, referrers {referrerScan.TotalSeconds:F1} s, statics {staticsScan.TotalSeconds:F1} s, root paths {pathSearch.TotalSeconds:F1} s");
    }

    /// <summary>Maps objects referenced directly by static fields to the field's name.</summary>
    /// <remarks>
    /// Statics live in runtime-owned arrays held by GC handles, so without this a path would start at an anonymous <c>System.Object[]</c>.
    /// Statics of generic instantiations are not mapped.
    /// </remarks>
    private static Dictionary<ulong, string> MapStaticFields(ClrRuntime runtime)
    {
        Dictionary<ulong, string> statics = [];
        foreach (ClrModule module in runtime.EnumerateModules())
        {
            foreach ((ulong methodTable, int _) in module.EnumerateTypeDefToMethodTableMap())
            {
                if (runtime.GetTypeByMethodTable(methodTable) is not { } type)
                    continue;

                foreach (ClrStaticField field in type.StaticFields)
                {
                    if (!field.IsObjectReference)
                        continue;

                    foreach (ClrAppDomain domain in runtime.AppDomains)
                    {
                        ulong address = field.ReadObject(domain).Address;
                        if (address != 0)
                            statics.TryAdd(address, $"{type.Name}.{field.Name}");
                    }
                }
            }
        }

        return statics;
    }

    private static List<KeyValuePair<string, long>> CountReferrers(ClrHeap heap, HashSet<ulong> targets)
    {
        Dictionary<string, long> referrers = [];
        foreach (ClrRoot root in heap.EnumerateRoots())
        {
            if (targets.Contains(root.Object.Address))
                Increment(referrers, $"[root: {root.RootKind}]");
        }

        foreach (ClrObject obj in heap.EnumerateObjects())
        {
            if (obj.IsFree || obj.Type is null)
                continue;

            foreach (ulong reference in obj.EnumerateReferenceAddresses(carefully: false, considerDependantHandles: false))
            {
                if (targets.Contains(reference))
                    Increment(referrers, obj.Type.Name ?? "?");
            }
        }

        List<KeyValuePair<string, long>> sorted = [.. referrers];
        sorted.Sort(static (a, b) => b.Value.CompareTo(a.Value));
        return sorted;
    }

    /// <remarks>
    /// Breadth-first search from all GC roots, so the first path found to each instance is a shortest one.
    /// Paths with the same sequence of types and fields are merged and counted, which surfaces the dominant retention pattern
    /// instead of many copies of it. The search follows plain reference addresses; field names are resolved only for the edges
    /// of reported paths, because resolving them for every reference makes the search two orders of magnitude slower.
    /// </remarks>
    private static List<RetentionPath> FindPaths(ClrHeap heap, HashSet<ulong> targets, Dictionary<ulong, string> statics, int maxPaths, long maxVisited, out bool complete)
    {
        Dictionary<ulong, ulong> parents = [];
        Dictionary<ulong, string> rootKinds = [];
        Queue<ulong> queue = new();

        foreach (ClrRoot root in heap.EnumerateRoots())
        {
            ulong address = root.Object.Address;
            if (address != 0 && rootKinds.TryAdd(address, root.RootKind.ToString()))
            {
                parents[address] = 0;
                queue.Enqueue(address);
            }
        }

        Dictionary<string, (RetentionPath Path, int Instances)> bySignature = [];
        int reportedTargets = 0;
        complete = true;

        while (queue.Count > 0)
        {
            ulong address = queue.Dequeue();
            if (targets.Contains(address))
            {
                RetentionPath path = BuildPath(heap, address, parents, rootKinds, statics);
                string signature = path.RootKind + "|" + string.Join("|", path.Steps.Select(static s => $"{s.Field}>{s.TypeName}"));
                bySignature[signature] = bySignature.TryGetValue(signature, out (RetentionPath Path, int Instances) existing)
                    ? (existing.Path, existing.Instances + 1)
                    : (path, 1);

                // Keep searching past maxPaths distinct patterns for a while so instance counts per pattern are meaningful.
                if (++reportedTargets >= Math.Max(maxPaths * 50, 1000) && bySignature.Count >= maxPaths)
                {
                    complete = false;
                    break;
                }
                continue;
            }

            ClrObject obj = heap.GetObject(address);
            if (obj.Type is null)
                continue;

            foreach (ulong child in obj.EnumerateReferenceAddresses(carefully: false, considerDependantHandles: true))
            {
                if (child != 0 && parents.TryAdd(child, address))
                    queue.Enqueue(child);
            }

            if (parents.Count >= maxVisited)
            {
                complete = false;
                break;
            }
        }

        List<RetentionPath> paths = [];
        foreach ((RetentionPath path, int instances) in bySignature.Values)
            paths.Add(path with { Instances = instances });
        paths.Sort(static (a, b) => b.Instances.CompareTo(a.Instances));
        return paths.Count > maxPaths ? paths[..maxPaths] : paths;
    }

    private static RetentionPath BuildPath(
        ClrHeap heap,
        ulong target,
        Dictionary<ulong, ulong> parents,
        Dictionary<ulong, string> rootKinds,
        Dictionary<ulong, string> statics)
    {
        List<PathStep> steps = [];
        ulong current = target;
        while (true)
        {
            ulong parent = parents[current];
            steps.Add(new PathStep(heap.GetObjectType(current)?.Name ?? "?", current, parent == 0 ? null : FieldName(heap, parent, current)));
            if (parent == 0)
                break;
            current = parent;
        }

        steps.Reverse();

        // Start the path at the outermost static field instead of the runtime's statics array that holds it.
        for (int i = 0; i < steps.Count; i++)
        {
            if (statics.TryGetValue(steps[i].Address, out string? staticField))
                return new RetentionPath($"static {staticField}", steps[i..], 1);
        }

        return new RetentionPath(rootKinds[current], steps, 1);
    }

    private static string? FieldName(ClrHeap heap, ulong parent, ulong child)
    {
        ClrObject obj = heap.GetObject(parent);
        if (obj.Type?.IsArray == true)
            return "[]";

        foreach (ClrReference reference in obj.EnumerateReferencesWithFields(carefully: false, considerDependantHandles: true))
        {
            if (reference.Object.Address == child)
                return reference.Field?.Name ?? (reference.IsDependentHandle ? "[dependent handle]" : null);
        }

        return null;
    }

    private static void Increment(Dictionary<string, long> counts, string key) => counts[key] = counts.GetValueOrDefault(key) + 1;
}
