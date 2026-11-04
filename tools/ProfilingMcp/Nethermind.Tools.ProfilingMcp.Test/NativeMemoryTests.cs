// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Tools.ProfilingMcp.Native;
using NUnit.Framework;

namespace Nethermind.Tools.ProfilingMcp.Test;

public class NativeMemoryTests
{
    private const string Smaps = """
        55d0a0000000-55d0a0021000 rw-p 00000000 00:00 0                          [heap]
        Size:                132 kB
        Rss:                 100 kB
        Swap:                  0 kB
        VmFlags: rd wr mr mw me ac sd
        7f0000200000-7f0000600000 rw-p 00000000 00:00 0
        Rss:                4096 kB
        Swap:                  8 kB
        7f0004000000-7f0004100000 rw-p 00000000 00:00 0
        Rss:                1024 kB
        7f0010000000-7f0010001000 r-xp 00000000 08:01 1234                       /usr/share/dotnet/libcoreclr.so
        Rss:                   4 kB
        7f0020000000-7f0020001000 r-xp 00000000 00:01 77                         /memfd:doublemapper (deleted)
        Rss:                   4 kB
        """;

    private static IReadOnlyList<Mapping> Parse() => NativeMemory.ParseSmaps(Smaps.Split('\n').Select(static l => l.TrimEnd('\r')));

    [Test]
    public void Parses_smaps_mappings_with_names_containing_spaces()
    {
        IReadOnlyList<Mapping> mappings = Parse();

        Assert.That(mappings, Is.EqualTo(new[]
        {
            new Mapping(0x55d0a0000000, 0x55d0a0021000, "rw-p", "[heap]", 100 * 1024, 0),
            new Mapping(0x7f0000200000, 0x7f0000600000, "rw-p", "", 4096 * 1024, 8 * 1024),
            new Mapping(0x7f0004000000, 0x7f0004100000, "rw-p", "", 1024 * 1024, 0),
            new Mapping(0x7f0010000000, 0x7f0010001000, "r-xp", "/usr/share/dotnet/libcoreclr.so", 4 * 1024, 0),
            new Mapping(0x7f0020000000, 0x7f0020001000, "r-xp", "/memfd:doublemapper (deleted)", 4 * 1024, 0),
        }));
    }

    [Test]
    public void Splits_anonymous_mappings_by_known_ranges_and_arena_alignment()
    {
        // The 4 MB mapping is a quarter gen2 region; the 1 MB one starts on a 64 MB boundary, as glibc's arena heaps do.
        KnownRange[] known = [new(0x7f0000200000, 0x7f0000300000, NativeMemory.GcPrefix + "gen2"), new(0x7f0000280000, 0x7f0000300000, NativeMemory.ThreadStacks)];
        Dictionary<string, long> rss = NativeMemory.Categorize(Parse(), known).ToDictionary(static c => c.Name, static c => c.Rss);

        Assert.That(rss, Is.EquivalentTo(new Dictionary<string, long>
        {
            [NativeMemory.OtherAnonymous] = 3 * 1024 * 1024,
            [NativeMemory.GcPrefix + "gen2"] = 1024 * 1024,
            [NativeMemory.MallocArenas] = 1024 * 1024,
            [NativeMemory.MallocMain] = 100 * 1024,
            [NativeMemory.NativeLibraries] = 4 * 1024,
            [NativeMemory.JitCode] = 4 * 1024,
        }));
    }

    [Test]
    public void Finds_mmapped_malloc_chunks_tiling_a_mapping()
    {
        Mapping mapping = new(0x10000, 0x20000, "rw-p", "", 0x10000, 0);
        Dictionary<ulong, (ulong, ulong)> headers = new()
        {
            [0x10000] = (0, 0x4000 | 2),
            [0x14000] = (0, 0x8000 | 2),
            [0x1c000] = (0, 0x4000 | 3), // PREV_INUSE: an arena chunk, not an mmapped one.
        };

        KnownRange[] chunks = [.. NativeMemory.MmappedChunks(mapping, a => headers.TryGetValue(a, out (ulong, ulong) h) ? h : null)];

        Assert.That(chunks, Is.EqualTo(new KnownRange[] { new(0x10000, 0x14000, NativeMemory.MallocMmapped), new(0x14000, 0x1c000, NativeMemory.MallocMmapped) }));
    }

    [TestCase("0 0x1 0x2 0x3 0x4 0x5 0x6 0x7ffd0000a000 0x7f0011223344", ExpectedResult = 0x7ffd0000a000UL)]
    [TestCase("-1 0x7ffd0000b000 0x7f0011223344\n", ExpectedResult = 0x7ffd0000b000UL)]
    [TestCase("running", ExpectedResult = null)]
    public ulong? Reads_the_stack_pointer_of_a_stopped_thread(string syscall) => NativeMemory.StackPointer(syscall);
}
