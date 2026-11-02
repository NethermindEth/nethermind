// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using Microsoft.Diagnostics.Runtime;

namespace Nethermind.Tools.ProfilingMcp.Memory;

/// <summary>Instance count and shallow size of one type on the managed heap.</summary>
/// <param name="LargeObjectBytes">Part of <paramref name="Bytes"/> on the large object heap.</param>
public sealed record TypeStat(string Name, long Count, long Bytes, long LargeObjectBytes);

/// <summary>
/// Per-type statistics of a managed heap at one point in time.
/// </summary>
/// <param name="FreeBytes">Free space between live objects; a large share relative to <paramref name="TotalBytes"/> indicates fragmentation.</param>
/// <param name="GenerationBytes">Object bytes per generation (gen0, gen1, gen2, LOH, POH, frozen).</param>
/// <param name="Types">Types sorted by descending <see cref="TypeStat.Bytes"/>.</param>
/// <remarks>Sizes are shallow: an object's size excludes the objects it references.</remarks>
public sealed record HeapSnapshot(
    string Target,
    DateTime TakenAtUtc,
    long ObjectCount,
    long TotalBytes,
    long FreeBytes,
    IReadOnlyDictionary<string, long> GenerationBytes,
    IReadOnlyList<TypeStat> Types)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    public static HeapSnapshot Collect(string target, ClrHeap heap)
    {
        Dictionary<ulong, (string Name, long Count, long Bytes, long LargeBytes)> byMethodTable = [];
        Dictionary<string, long> generations = [];
        long objectCount = 0;
        long totalBytes = 0;
        long freeBytes = 0;

        foreach (ClrSegment segment in heap.Segments)
        {
            foreach (ClrObject obj in segment.EnumerateObjects())
            {
                if (!obj.IsValid || obj.Type is not ClrType type)
                    continue;

                long size = (long)obj.Size;
                if (obj.IsFree)
                {
                    freeBytes += size;
                    continue;
                }

                Generation generation = segment.GetGeneration(obj.Address);
                string generationName = GenerationName(generation);
                generations[generationName] = generations.GetValueOrDefault(generationName) + size;

                objectCount++;
                totalBytes += size;
                long largeBytes = generation == Generation.Large ? size : 0;
                byMethodTable[type.MethodTable] = byMethodTable.TryGetValue(type.MethodTable, out (string Name, long Count, long Bytes, long LargeBytes) stat)
                    ? (stat.Name, stat.Count + 1, stat.Bytes + size, stat.LargeBytes + largeBytes)
                    : (type.Name ?? $"<unknown 0x{type.MethodTable:x}>", 1, size, largeBytes);
            }
        }

        // Generic instantiations from different modules can share a name; merge them so diffs line up by name.
        Dictionary<string, TypeStat> byName = [];
        foreach ((string name, long count, long bytes, long largeBytes) in byMethodTable.Values)
        {
            byName[name] = byName.TryGetValue(name, out TypeStat? existing)
                ? existing with { Count = existing.Count + count, Bytes = existing.Bytes + bytes, LargeObjectBytes = existing.LargeObjectBytes + largeBytes }
                : new TypeStat(name, count, bytes, largeBytes);
        }

        List<TypeStat> types = [.. byName.Values];
        types.Sort(static (a, b) => b.Bytes.CompareTo(a.Bytes));
        return new HeapSnapshot(target, DateTime.UtcNow, objectCount, totalBytes, freeBytes, generations, types);
    }

    /// <summary>Returns per-type changes from <paramref name="baseline"/> to <paramref name="current"/>, largest absolute byte change first.</summary>
    public static IReadOnlyList<TypeDelta> Diff(HeapSnapshot baseline, HeapSnapshot current)
    {
        Dictionary<string, TypeStat> before = [];
        foreach (TypeStat type in baseline.Types)
            before[type.Name] = type;

        List<TypeDelta> deltas = [];
        foreach (TypeStat after in current.Types)
        {
            before.Remove(after.Name, out TypeStat? old);
            deltas.Add(new TypeDelta(after.Name, after.Count - (old?.Count ?? 0), after.Bytes - (old?.Bytes ?? 0), after.Count, after.Bytes));
        }

        foreach (TypeStat gone in before.Values)
            deltas.Add(new TypeDelta(gone.Name, -gone.Count, -gone.Bytes, 0, 0));

        deltas.RemoveAll(static d => d.CountDelta == 0 && d.BytesDelta == 0);
        deltas.Sort(static (a, b) => Math.Abs(b.BytesDelta).CompareTo(Math.Abs(a.BytesDelta)));
        return deltas;
    }

    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));

    public static HeapSnapshot Load(string path) =>
        JsonSerializer.Deserialize<HeapSnapshot>(File.ReadAllText(path), JsonOptions) ?? throw new InvalidDataException($"Empty heap snapshot {path}");

    internal static string GenerationName(Generation generation) => generation switch
    {
        Generation.Generation0 => "gen0",
        Generation.Generation1 => "gen1",
        Generation.Generation2 => "gen2",
        Generation.Large => "LOH",
        Generation.Pinned => "POH",
        Generation.Frozen => "frozen",
        _ => "unknown",
    };
}
