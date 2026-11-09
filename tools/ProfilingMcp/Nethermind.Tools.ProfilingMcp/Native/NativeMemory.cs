// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Globalization;
using Microsoft.Diagnostics.Runtime;
using Microsoft.Win32.SafeHandles;
using Nethermind.Tools.ProfilingMcp.Memory;

namespace Nethermind.Tools.ProfilingMcp.Native;

/// <summary>One mapping of a process's address space, from <c>/proc/&lt;pid&gt;/smaps</c>, with its resident and swapped-out bytes.</summary>
public sealed record Mapping(ulong Start, ulong End, string Permissions, string Name, long Rss, long Swap)
{
    public bool IsAnonymous => Name.Length == 0;
}

/// <summary>A labelled address range: a GC region, a runtime heap or a thread's stack.</summary>
/// <param name="Detail">What the range is within its label, such as the thread a stack belongs to; null to describe it by its mapping.</param>
public readonly record struct KnownRange(ulong Start, ulong End, string Label, string? Detail = null);

/// <summary>The part of one mapping's memory that one category holds; <paramref name="Item"/> names the file, thread or mapping.</summary>
public sealed record MemoryShare(string Category, string Item, double Rss, double Swap);

/// <summary>Resident memory of one category.</summary>
public sealed record MemoryCategory(string Name, long Rss, long Swap, int Mappings);

/// <summary>Splits a Linux process's resident memory into what holds it: the GC heap, the .NET runtime, thread stacks, malloc and mapped files.</summary>
/// <remarks>
/// Every mapping in <c>smaps</c> is attributed by address. GC regions, runtime heaps and managed thread stacks come from the runtime through
/// ClrMD, read without suspending the process; the stacks of all threads come from the stack pointers in <c>/proc/&lt;pid&gt;/task/*/syscall</c>.
/// smaps reports residency per mapping only, so a mapping that is partly a known range is split in proportion to the bytes in each.
/// glibc's malloc blocks over its mmap threshold are found by their chunk headers. Anonymous memory left over is glibc's malloc when it lies
/// in a 64 MB-aligned arena heap or <c>[heap]</c>, and otherwise another allocator's or a native library's own mmaps.
/// </remarks>
public static class NativeMemory
{
    public const string GcPrefix = "GC heap: ";
    public const string RuntimePrefix = ".NET runtime: ";
    public const string ThreadStacks = "thread stacks";
    public const string MallocArenas = "malloc arenas (native heap)";
    public const string MallocMain = "malloc main arena ([heap])";
    public const string MallocMmapped = "malloc large blocks (mmapped on their own)";
    public const string OtherAnonymous = "other anonymous mmaps (other allocators such as jemalloc, native libraries' own)";
    public const string JitCode = RuntimePrefix + "executable memory (JIT code, stubs)";
    public const string Assemblies = "files: .NET assemblies";
    public const string NativeLibraries = "files: native libraries";
    public const string OtherFiles = "files: other";

    /// <summary>glibc's <c>HEAP_MAX_SIZE</c> on 64-bit: every non-main arena heap is a mapping aligned to it.</summary>
    private const ulong ArenaHeapSize = 64 << 20;

    public static IReadOnlyList<Mapping> ParseSmaps(IEnumerable<string> lines)
    {
        List<Mapping> mappings = [];
        (ulong Start, ulong End, string Permissions, string Name)? current = null;
        long rss = 0, swap = 0;
        foreach (string line in lines)
        {
            if (TryParseHeader(line) is { } header)
            {
                if (current is { } done)
                    mappings.Add(new Mapping(done.Start, done.End, done.Permissions, done.Name, rss, swap));
                current = header;
                rss = swap = 0;
            }
            else if (line.StartsWith("Rss:", StringComparison.Ordinal))
            {
                rss = Kilobytes(line);
            }
            else if (line.StartsWith("Swap:", StringComparison.Ordinal))
            {
                swap = Kilobytes(line);
            }
        }

        if (current is { } last)
            mappings.Add(new Mapping(last.Start, last.End, last.Permissions, last.Name, rss, swap));
        return mappings;
    }

    /// <summary>Parses <c>start-end perms offset dev inode [name]</c>; the name may contain spaces.</summary>
    private static (ulong, ulong, string, string)? TryParseHeader(string line)
    {
        int dash = line.IndexOf('-');
        int space = line.IndexOf(' ');
        if (dash <= 0 || space < dash
            || !ulong.TryParse(line.AsSpan(0, dash), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ulong start)
            || !ulong.TryParse(line.AsSpan(dash + 1, space - dash - 1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ulong end))
            return null;

        string[] fields = line.Split(' ', 6, StringSplitOptions.RemoveEmptyEntries);
        return fields.Length < 5 ? null : (start, end, fields[1], fields.Length == 6 ? fields[5].Trim() : "");
    }

    private static long Kilobytes(string line) =>
        long.TryParse(line.AsSpan(line.IndexOf(':') + 1).Trim().TrimEnd("kB").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long kb) ? kb * 1024 : 0;

    /// <summary>Splits every mapping's resident and swapped-out bytes among the categories and items that hold them.</summary>
    /// <param name="known">GC regions, runtime heaps and thread stacks; where they overlap, the bytes count once, for the range that starts lower.</param>
    public static IReadOnlyList<MemoryShare> Attribute(IReadOnlyList<Mapping> mappings, IReadOnlyList<KnownRange> known)
    {
        List<MemoryShare> shares = [];
        KnownRange[] ranges = [.. known.OrderBy(static r => r.Start)];
        foreach (Mapping mapping in mappings)
        {
            if (mapping.Rss == 0 && mapping.Swap == 0)
                continue;

            if (!mapping.IsAnonymous)
            {
                shares.Add(new MemoryShare(NamedCategory(mapping.Name), mapping.Name, mapping.Rss, mapping.Swap));
                continue;
            }

            double size = mapping.End - mapping.Start;
            double covered = 0;
            foreach (((string label, string? detail), ulong bytes) in Overlaps(mapping, ranges))
            {
                shares.Add(new MemoryShare(label, detail ?? Describe(mapping), mapping.Rss * (bytes / size), mapping.Swap * (bytes / size)));
                covered += bytes;
            }

            if (covered < size)
                shares.Add(new MemoryShare(AnonymousCategory(mapping), Describe(mapping), mapping.Rss * ((size - covered) / size), mapping.Swap * ((size - covered) / size)));
        }

        return shares;
    }

    /// <summary>Totals shares by category, largest first.</summary>
    public static IReadOnlyList<MemoryCategory> Categorize(IReadOnlyList<MemoryShare> shares)
    {
        Dictionary<string, int> indexes = new(StringComparer.Ordinal);
        List<(string Name, double Rss, double Swap, int Mappings)> totals = [];
        foreach (MemoryShare share in shares)
        {
            if (!indexes.TryGetValue(share.Category, out int index))
            {
                indexes[share.Category] = index = totals.Count;
                totals.Add((share.Category, 0, 0, 0));
            }

            (string name, double rss, double swap, int mappings) = totals[index];
            totals[index] = (name, rss + share.Rss, swap + share.Swap, mappings + 1);
        }

        List<MemoryCategory> categories = new(totals.Count);
        foreach ((string name, double rss, double swap, int mappings) in totals)
            categories.Add(new MemoryCategory(name, (long)rss, (long)swap, mappings));
        Sorting.SortDescending(categories, static c => c.Rss + c.Swap);
        return categories;
    }

    private static string Describe(Mapping mapping) =>
        $"mapping at {mapping.Start:x}, {(mapping.End - mapping.Start) / (1024.0 * 1024):N1} MB";

    /// <summary>Bytes of <paramref name="mapping"/> in each label and detail, counting each byte once.</summary>
    private static Dictionary<(string Label, string? Detail), ulong> Overlaps(Mapping mapping, KnownRange[] sortedRanges)
    {
        Dictionary<(string, string?), ulong> bytes = [];
        ulong next = mapping.Start;
        foreach (KnownRange range in sortedRanges)
        {
            if (range.Start >= mapping.End)
                break;

            ulong start = Math.Max(Math.Max(range.Start, mapping.Start), next);
            ulong end = Math.Min(range.End, mapping.End);
            if (end <= start)
                continue;

            bytes[(range.Label, range.Detail)] = bytes.GetValueOrDefault((range.Label, range.Detail)) + (end - start);
            next = end;
        }

        return bytes;
    }

    private static string AnonymousCategory(Mapping mapping) =>
        mapping.Start % ArenaHeapSize == 0 && mapping.End - mapping.Start <= ArenaHeapSize ? MallocArenas : OtherAnonymous;

    private static string NamedCategory(string name) => name switch
    {
        "[heap]" => MallocMain,
        "[stack]" => ThreadStacks,
        _ when name.StartsWith("/memfd:doublemapper", StringComparison.Ordinal) => JitCode,
        _ when name.StartsWith('[') => $"kernel-provided {name}",
        _ when name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) => Assemblies,
        _ when name.Contains(".so", StringComparison.Ordinal) => NativeLibraries,
        _ => OtherFiles,
    };

    /// <summary>The blocks glibc's malloc mapped on their own, found by their chunk headers in the process's anonymous mappings.</summary>
    /// <remarks>Reading another process's memory needs ptrace access; without it no blocks are found.</remarks>
    public static IReadOnlyList<KnownRange> MallocMmappedRanges(int pid, IReadOnlyList<Mapping> mappings)
    {
        List<KnownRange> ranges = [];
        try
        {
            using SafeFileHandle memory = File.OpenHandle($"/proc/{pid}/mem");
            byte[] header = new byte[16];
            (ulong, ulong)? Read(ulong address)
            {
                try
                {
                    return RandomAccess.Read(memory, header, (long)address) == header.Length
                        ? (BinaryPrimitives.ReadUInt64LittleEndian(header), BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(8)))
                        : null;
                }
                catch (IOException)
                {
                    return null;
                }
            }

            foreach (Mapping mapping in mappings)
            {
                if (mapping.IsAnonymous && mapping.Rss > 0 && mapping.Permissions.StartsWith("rw", StringComparison.Ordinal))
                    ranges.AddRange(MmappedChunks(mapping, Read));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }

        return ranges;
    }

    /// <summary>Walks the mmapped malloc chunks that tile <paramref name="mapping"/> from its start; the kernel merges adjacent ones into one mapping.</summary>
    /// <remarks>
    /// glibc starts a block it maps on its own with a chunk header: a zero <c>prev_size</c> and a size that is a whole number of pages,
    /// flagged <c>IS_MMAPPED</c> and nothing else. The walk stops at the first place that does not hold such a header.
    /// </remarks>
    /// <param name="readHeader">Reads the two 8-byte words at an address, or null when they cannot be read.</param>
    internal static IEnumerable<KnownRange> MmappedChunks(Mapping mapping, Func<ulong, (ulong PrevSize, ulong Size)?> readHeader)
    {
        const ulong IsMmapped = 2, Flags = 7, Page = 4096;
        ulong at = mapping.Start;
        while (at < mapping.End && readHeader(at) is (0, var size) && (size & Flags) == IsMmapped)
        {
            ulong length = size & ~Flags;
            if (length == 0 || length % Page != 0 || length > mapping.End - at)
                break;

            yield return new KnownRange(at, at + length, MallocMmapped);
            at += length;
        }
    }

    /// <summary>The ranges of every thread's stack, found from the stack pointer each thread last stopped at.</summary>
    /// <remarks>A thread running at the moment of reading reports no stack pointer; ClrMD's ranges cover the managed ones among them.</remarks>
    public static IEnumerable<KnownRange> ThreadStackRanges(int pid, IReadOnlyList<Mapping> mappings)
    {
        Mapping[] sorted = [.. mappings.OrderBy(static m => m.Start)];
        foreach (string task in Directory.EnumerateDirectories($"/proc/{pid}/task"))
        {
            string syscall;
            try
            {
                syscall = File.ReadAllText(Path.Combine(task, "syscall"));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            if (StackPointer(syscall) is { } sp && Find(sorted, sp) is { IsAnonymous: true } stack)
                yield return new KnownRange(stack.Start, stack.End, ThreadStacks, ThreadName(pid, Path.GetFileName(task)));
        }
    }

    private static string ThreadName(int pid, string tid)
    {
        try
        {
            return $"thread {File.ReadAllText($"/proc/{pid}/task/{tid}/comm").TrimEnd()} ({tid})";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return $"thread {tid}";
        }
    }

    /// <summary>Reads the stack pointer from <c>/proc/&lt;pid&gt;/task/&lt;tid&gt;/syscall</c>: <c>nr args... sp pc</c>, or <c>-1 sp pc</c> outside a syscall.</summary>
    internal static ulong? StackPointer(string syscall)
    {
        string[] fields = syscall.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return fields.Length >= 3 && fields[^2].StartsWith("0x", StringComparison.Ordinal)
               && ulong.TryParse(fields[^2].AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ulong sp)
            ? sp
            : null;
    }

    private static Mapping? Find(Mapping[] sorted, ulong address)
    {
        int low = 0, high = sorted.Length - 1;
        while (low <= high)
        {
            int middle = (low + high) >>> 1;
            if (sorted[middle].End <= address)
                low = middle + 1;
            else if (sorted[middle].Start > address)
                high = middle - 1;
            else
                return sorted[middle];
        }

        return null;
    }

    /// <summary>GC regions, runtime heaps and managed thread stacks of a running .NET process, read with ClrMD without suspending it.</summary>
    /// <returns>The ranges and the GC's committed bytes; null when the process is not .NET or ClrMD cannot read it.</returns>
    public static (IReadOnlyList<KnownRange> Ranges, long GcCommitted)? RuntimeRanges(int pid)
    {
        try
        {
            using DataTarget dataTarget = HeapTarget.AttachToProcess(pid);
            using ClrRuntime runtime = HeapTarget.CreateRuntime(dataTarget, requireWalkableHeap: false);
            List<KnownRange> ranges = [];
            long committed = 0;
            foreach (ClrSegment segment in runtime.Heap.Segments)
            {
                MemoryRange memory = segment.CommittedMemory;
                ranges.Add(new KnownRange(memory.Start, memory.End, GcPrefix + segment.Kind switch
                {
                    GCSegmentKind.Generation0 => "gen0",
                    GCSegmentKind.Generation1 => "gen1",
                    GCSegmentKind.Generation2 => "gen2",
                    GCSegmentKind.Large => "large objects",
                    GCSegmentKind.Pinned => "pinned objects",
                    GCSegmentKind.Frozen => "frozen objects",
                    _ => "ephemeral",
                }));
                committed += (long)memory.Length;
            }

            foreach (ClrNativeHeapInfo heap in runtime.EnumerateClrNativeHeaps())
            {
                MemoryRange memory = heap.MemoryRange;
                if (memory.Length > 0)
                    ranges.Add(new KnownRange(memory.Start, memory.End, RuntimeHeapName(heap.Kind)));
            }

            foreach (ClrThread thread in runtime.Threads)
            {
                if (thread.StackLimit != 0 && thread.StackBase > thread.StackLimit)
                    ranges.Add(new KnownRange(thread.StackLimit, thread.StackBase, ThreadStacks, ThreadName(pid, thread.OSThreadId.ToString(CultureInfo.InvariantCulture))));
            }

            return (ranges, committed);
        }
        catch (Exception e) when (e is InvalidOperationException or ClrDiagnosticsException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    private static string RuntimeHeapName(NativeHeapKind kind) => kind switch
    {
        NativeHeapKind.GCFreeRegion => GcPrefix + "free regions",
        NativeHeapKind.GCBookkeeping => RuntimePrefix + "GC bookkeeping (card tables, mark arrays)",
        NativeHeapKind.HandleTable => RuntimePrefix + "GC handle table",
        NativeHeapKind.LoaderCodeHeap or NativeHeapKind.HostCodeHeap => JitCode,
        _ => RuntimePrefix + "loader heaps (type data, stubs)",
    };
}
