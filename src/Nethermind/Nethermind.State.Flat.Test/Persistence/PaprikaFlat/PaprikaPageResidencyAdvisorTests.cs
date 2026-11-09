// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.IO;
using Nethermind.Logging;
using Nethermind.State.Flat.Persistence.PaprikaFlat;
using Nethermind.State.Flat.PersistedSnapshots.Storage;
using NUnit.Framework;
using Paprika;
using Paprika.Crypto;
using Paprika.Data;
using Paprika.Store;
using Paprika.Store.PageManagers;

namespace Nethermind.State.Flat.Test.Persistence.PaprikaFlat;

public partial class PaprikaPageResidencyAdvisorTests
{
    private const long FileSize = 64 * 1024 * 1024;
    private const long SmallFileSize = 16 * 1024 * 1024;
    private const byte HistoryDepth = PaprikaFlatStore.HistoryDepth;
    private const int TimeoutMs = 30_000;
    private const int MadvRandom = 1;
    private const int MadvDontNeed = 4;
    private static readonly int UnitBytes = PaprikaPageResidencyAdvisor.UnitBytes;

    internal const string RssUnboundedByFaultAround =
        "A read fault also maps up to fault_around_bytes (64 KiB) of neighbours already in the page cache, which a 4 KiB unit leaves untracked, so the RSS of the mapping exceeds the budget";

    private TempPath _directory = null!;

    private string PaprikaFile => MemoryMappedPageManager.GetPaprikaFilePath(_directory.Path);

    [SetUp]
    public void Setup()
    {
        _directory = TempPath.GetTempDirectory();
        if (!OperatingSystem.IsLinux()) Assert.Ignore("madvise, mincore and /proc/self/smaps are Linux only");
    }

    [TearDown]
    public void TearDown() => _directory.Dispose();

    [Test]
    public async Task Concurrent_readers_and_writer_keep_eviction_accounting_exact([Values(1, 8)] int readers)
    {
        const int capacity = 16;
        const int writeBatch = 250;
        Keccak[] loaded = Keys(0, 20_000);
        List<Keccak> written = [];
        PaprikaPageResidencyAdvisor advisor = NewAdvisor(capacity, ringCapacity: 1024);
        using PagedDb db = PagedDb.MemoryMappedDb(FileSize, HistoryDepth, _directory.Path, advisor);
        await Write(db, loaded, CommitOptions.FlushDataOnly);

        Task writer = Task.Run(async () =>
        {
            Stopwatch elapsed = Stopwatch.StartNew();
            while (elapsed.Elapsed < TimeSpan.FromSeconds(2))
            {
                Keccak[] keys = Keys(loaded.Length + written.Count, writeBatch);
                await Write(db, keys, CommitOptions.FlushDataOnly);
                written.AddRange(keys);
            }
        });
        Task<int>[] reads = [.. Enumerable.Range(0, readers).Select(seed => Task.Run(() => ReadWhile(db, loaded, seed, writer)))];
        await writer;
        int[] readMismatches = await Task.WhenAll(reads);

        using IReadOnlyBatch reader = db.BeginReadOnlyBatch();
        int mismatches = Mismatches(reader, loaded) + Mismatches(reader, written);
        AssertDrained(advisor);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(readMismatches.Sum() + mismatches, Is.Zero, "every value read back intact");
            Assert.That(advisor.Queued + advisor.InlineFallback, Is.Positive, "units were evicted");
            Assert.That(advisor.Tracker.ResidentBytes, Is.EqualTo(advisor.Tracker.Count * (long)Environment.SystemPageSize), "the resident count matches the tracked units");
            Assert.That(advisor.ResidentBytes, Is.LessThanOrEqualTo((long)capacity * UnitBytes));
            Assert.That(advisor.Tracker.ContainsPage(0, 0), Is.False, "the unit of the root pages is never tracked");
        }
    }

    public enum PageCache
    {
        Cold,
        Warm,
    }

    [Test]
    [NonParallelizable]
    [Ignore(RssUnboundedByFaultAround)]
    public void Mapping_rss_stays_within_budget([Values] PageCache pageCache, [Values] bool budget)
    {
        const int threads = 8;
        const int budgetUnits = 16;
        IgnoreWherePageCacheCannotBeDropped();
        string path = WriteFile(FileSize);
        if (pageCache == PageCache.Cold) DropFromPageCache(path);
        else ReadWhole(path);
        Assert.That(CachedPages(path), Is.EqualTo(pageCache == PageCache.Cold ? 0 : FileSize / Environment.SystemPageSize));

        PaprikaPageResidencyAdvisor? advisor = budget ? PaprikaPageResidencyAdvisor.Create(budgetUnits * (long)UnitBytes, HistoryDepth, LimboLogs.Instance) : null;
        using MemoryMappedPageManager manager = new(FileSize, HistoryDepth, _directory.Path, advisor, PersistenceOptions.MMapOnly);
        (int touched, int mismatches) = Touch(manager, threads, accessesPerThread: 1000);
        if (advisor is not null) AssertDrained(advisor);

        // A unit read between its eviction and its release faults back in untracked; the race can recur, so a unit
        // per reader bounds it only in practice. The units of the root pages are never tracked.
        long bound = (budgetUnits + threads + HistoryDepth) * (long)UnitBytes;
        long rss = Rss(path);
        TestContext.Out.WriteLine($"{pageCache}, budget {budget}: RSS {rss} bytes, bound {bound}, {touched} distinct pages touched ({touched * (long)Page.PageSize} bytes)");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(mismatches, Is.Zero, "every page read back intact");
            if (budget) Assert.That(rss, Is.LessThanOrEqualTo(bound));
            else Assert.That(rss, Is.GreaterThan(bound), "without the advisor the same reads exceed the bound");
        }
    }

    [Test]
    [Ignore(RssUnboundedByFaultAround)]
    public async Task Evicted_dirty_pages_survive_flush_and_reopen([Values] CommitOptions options)
    {
        IgnoreWherePageCacheCannotBeDropped();
        Keccak[] keys = Keys(0, 20_000);
        // The minimum of 8 units, so most of the batch is released before it commits.
        PaprikaPageResidencyAdvisor advisor = PaprikaPageResidencyAdvisor.Create(UnitBytes, HistoryDepth, LimboLogs.Instance)!;
        using (PagedDb db = PagedDb.MemoryMappedDb(FileSize, HistoryDepth, _directory.Path, advisor))
        {
            using IBatch batch = db.BeginNextBatch();
            foreach (Keccak key in keys) batch.SetRaw(Key.Account(key), key.Span);
            Assert.That(() => advisor.Dispatched, Is.Positive.After(TimeoutMs, 10), "dirty units were released before the commit");
            AssertDrained(advisor);
            // The 8 units tracked, the root pages', and one the writer may read back before its release.
            Assert.That(Rss(PaprikaFile), Is.LessThanOrEqualTo((8 + 1 + HistoryDepth) * (long)UnitBytes), "the released dirty units left the mapping");
            long evicted = advisor.Queued + advisor.InlineFallback;
            long released = advisor.Dispatched;

            await batch.Commit(options);
            TestContext.Out.WriteLine($"{options}: {evicted} units evicted and {released} released before the commit, which wrote {db.NextFreePage * (long)Page.PageSize / UnitBytes} units");
            if (options != CommitOptions.FlushDataAndRoot) db.Flush();
            Assert.That(Permissions(PaprikaFile), Is.EqualTo("rw-s"));
        }

        DropFromPageCache(PaprikaFile);
        Assert.That(CachedPages(PaprikaFile), Is.Zero, "the reopened store reads the disk");
        using PagedDb reopened = PagedDb.MemoryMappedDb(FileSize, HistoryDepth, _directory.Path, accessObserver: null);
        using IReadOnlyBatch reader = reopened.BeginReadOnlyBatch();
        Assert.That(Mismatches(reader, keys), Is.Zero);
    }

    /// <summary>The control of <see cref="Evicted_dirty_pages_survive_flush_and_reopen"/>: a private mapping loses its dirty data.</summary>
    [TestCase(MemoryMappedFileAccess.ReadWrite, 's', true)]
    [TestCase(MemoryMappedFileAccess.CopyOnWrite, 'p', false)]
    public unsafe void Dont_need_keeps_dirty_data_only_in_a_shared_mapping(MemoryMappedFileAccess access, char sharing, bool kept)
    {
        byte[] pattern = Bytes.FromHexString("0x0123456789abcdeffedcba9876543210");
        string path = WriteFile(Environment.SystemPageSize);
        using MemoryMappedFile file = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0, access);
        using MemoryMappedViewAccessor view = file.CreateViewAccessor(0, 0, access);
        byte* pointer = null;
        view.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
        try
        {
            pattern.CopyTo(new Span<byte>(pointer, pattern.Length));
            Assert.That(Madvise(pointer, (nuint)Environment.SystemPageSize, MadvDontNeed), Is.Zero);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(Permissions(path)[3], Is.EqualTo(sharing));
                Assert.That(new Span<byte>(pointer, pattern.Length).SequenceEqual(pattern), Is.EqualTo(kept));
            }
        }
        finally
        {
            view.SafeMemoryMappedViewHandle.ReleasePointer();
        }
    }

    public enum Disposal
    {
        NeverMapped,
        MappedAndChurned,
        AdvisorDisposedFirst,
        DisposedTwice,
    }

    [Test]
    public void Disposal_stops_drain_before_unmap([Values] Disposal disposal)
    {
        WriteFile(SmallFileSize);
        for (int iteration = 0; iteration < 20; iteration++)
        {
            PaprikaPageResidencyAdvisor advisor = NewAdvisor(capacity: 8, ringCapacity: 1024);
            DisposalProbe probe = new(advisor);
            if (disposal == Disposal.NeverMapped)
            {
                probe.Dispose();
            }
            else
            {
                MemoryMappedPageManager manager = new(SmallFileSize, HistoryDepth, _directory.Path, probe, PersistenceOptions.MMapOnly);
                Touch(manager, threads: 4, accessesPerThread: 500);
                Assert.That(advisor.Queued + advisor.InlineFallback, Is.Positive, "units were evicted");
                if (disposal == Disposal.AdvisorDisposedFirst) probe.Dispose();
                manager.Dispose();
                if (disposal == Disposal.DisposedTwice)
                {
                    manager.Dispose();
                    probe.Dispose();
                }
            }

            Assert.That(probe.DrainStoppedOnDispose, Is.True, "the drain stopped before Dispose returned, so before the unmap");
            long evicted = advisor.Queued + advisor.InlineFallback;
            for (uint page = HistoryDepth; page < 64 * UnitBytes / Page.PageSize; page++) advisor.OnAccess(page);
            Assert.That(advisor.Queued + advisor.InlineFallback, Is.EqualTo(evicted), "a disposed advisor tracks nothing");
        }
    }

    [TestCase(4096, 65536, 2, 0, 1u, 16u)]
    [TestCase(4096, 65536, 2, 15, 2u, 17u)]
    [TestCase(16384, 65536, 2, 3, 1u, 4u)]
    [TestCase(65536, 65536, 2, 0, 1u, 16u)]
    [TestCase(4096, 4096, 2, 0, 2u, 2u)]
    [TestCase(4096, 65536, 17, 0, 2u, 32u)]
    public unsafe void Unit_mapping_excludes_root_pages(int osPageBytes, int unitBytes, byte historyDepth, int skewOsPages, uint firstTrackedUnit, uint firstTrackedPage)
    {
        uint pagesPerUnit = (uint)(unitBytes / Page.PageSize);
        long mappedBytes = (firstTrackedPage + 2 * pagesPerUnit) * (long)Page.PageSize;
        // Real memory, though nothing is evicted, so nothing is released.
        byte* aligned = (byte*)NativeMemory.AlignedAlloc((nuint)(unitBytes + mappedBytes), (nuint)unitBytes);
        try
        {
            PageResidencyTracker tracker = new(64);
            using PaprikaPageResidencyAdvisor advisor = new(tracker, historyDepth, unitBytes, 1024, LimboLogs.Instance.GetClassLogger<PaprikaPageResidencyAdvisor>());
            advisor.OnMapped((nint)(aligned + skewOsPages * osPageBytes), mappedBytes);

            for (uint page = 0; page < firstTrackedPage; page++) advisor.OnAccess(page);
            int rootsTracked = tracker.Count;
            for (uint page = firstTrackedPage; page < firstTrackedPage + pagesPerUnit; page++) advisor.OnAccess(page);
            int firstUnitTracked = tracker.Count;
            advisor.OnAccess(firstTrackedPage + pagesPerUnit);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(rootsTracked, Is.Zero, "the units of the root pages are never tracked");
                Assert.That(firstUnitTracked, Is.EqualTo(1), "the pages of one unit share it");
                Assert.That(tracker.Count, Is.EqualTo(2));
                Assert.That(tracker.ContainsPage(0, firstTrackedUnit), Is.True);
                Assert.That(tracker.ContainsPage(0, firstTrackedUnit + 1), Is.True);
            }
        }
        finally
        {
            NativeMemory.AlignedFree(aligned);
        }
    }

    [TestCase(-1L, 0L, 0, 0)]
    [TestCase(0L, 0L, 0, 0)]
    [TestCase(0L, 1L, 8, 8192)]
    [TestCase(12L, 0L, 8, 8192)]
    [TestCase(16L, -1L, 8, 8192)]
    [TestCase(16L, 0L, 16, 8192)]
    [TestCase(17L, 0L, 16, 8192)]
    [TestCase(65536L, 0L, 65536, 8192)]
    [TestCase(1L << 20, 0L, 1 << 20, 131072)]
    public void Create_sizes_capacity_within_budget(long budgetUnits, long extraBytes, int expectedCapacity, int expectedRingCapacity)
    {
        long budget = budgetUnits * UnitBytes + extraBytes;
        using PaprikaPageResidencyAdvisor? advisor = PaprikaPageResidencyAdvisor.Create(budget, HistoryDepth, LimboLogs.Instance);
        int capacity = advisor?.Tracker.MaxCapacity ?? 0;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(capacity, Is.EqualTo(expectedCapacity));
            Assert.That((long)capacity * UnitBytes, Is.LessThanOrEqualTo(Math.Max(budget, 8L * UnitBytes)), "the budget is not exceeded beyond the minimum of 8 units");
            Assert.That(advisor?.RingCapacity ?? 0, Is.EqualTo(expectedRingCapacity));
        }
    }

    [Test]
    public void Full_queue_releases_inline_and_keeps_accounting_exact()
    {
        WriteFile(SmallFileSize);
        PaprikaPageResidencyAdvisor advisor = NewAdvisor(capacity: 8, ringCapacity: 2);
        using MemoryMappedPageManager manager = new(SmallFileSize, HistoryDepth, _directory.Path, advisor, PersistenceOptions.MMapOnly);
        (_, int mismatches) = Touch(manager, threads: 8, accessesPerThread: 2000);
        AssertDrained(advisor);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(mismatches, Is.Zero, "every page read back intact");
            Assert.That(advisor.InlineFallback, Is.Positive);
            Assert.That(advisor.Queued, Is.Positive);
        }
    }

    [Test]
    public unsafe void Cold_fault_does_not_read_around_neighbours([Values] bool madvRandom)
    {
        const int neighbours = 16;
        IgnoreWherePageCacheCannotBeDropped();
        string path = WriteFile(SmallFileSize);
        int pageSize = Environment.SystemPageSize;
        // Paprika's flags: a shared mapping of a file opened for random access.
        using SafeFileHandle handle = File.OpenHandle(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None, FileOptions.RandomAccess);
        using MemoryMappedFile file = MemoryMappedFile.CreateFromFile(handle, null, SmallFileSize, MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, leaveOpen: true);
        using MemoryMappedViewAccessor view = file.CreateViewAccessor();
        byte* pointer = null;
        view.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
        try
        {
            if (madvRandom) Assert.That(Madvise(pointer, (nuint)SmallFileSize, MadvRandom), Is.Zero);
            PosixReclaim.FadviseDontNeed((int)handle.DangerousGetHandle(), 0, SmallFileSize);
            Assert.That(CachedPages(pointer, SmallFileSize), Is.Zero, "the file starts cold");

            byte* middle = pointer + SmallFileSize / 2;
            _ = Volatile.Read(ref *middle);
            int faulted = CachedPages(middle, pageSize);
            int cachedNeighbours = CachedPages(middle - neighbours * pageSize, 2 * neighbours * pageSize + pageSize) - faulted;
            TestContext.Out.WriteLine($"MADV_RANDOM {madvRandom}: {cachedNeighbours} of {2 * neighbours} neighbours read with the faulting page");
            if (!madvRandom && cachedNeighbours == 0) Assert.Ignore("This host does not read around on mmap faults, so there is nothing for MADV_RANDOM to stop");
            using (Assert.EnterMultipleScope())
            {
                Assert.That(faulted, Is.EqualTo(1));
                if (madvRandom) Assert.That(cachedNeighbours, Is.Zero);
            }
        }
        finally
        {
            view.SafeMemoryMappedViewHandle.ReleasePointer();
        }
    }

    /// <summary>Forwards to <paramref name="advisor"/>, recording whether its drain had stopped when its first <see cref="Dispose"/> returned.</summary>
    private sealed class DisposalProbe(PaprikaPageResidencyAdvisor advisor) : IPageAccessObserver
    {
        public bool? DrainStoppedOnDispose { get; private set; }

        public void OnMapped(nint basePointer, long mappedBytes) => advisor.OnMapped(basePointer, mappedBytes);

        public void OnAccess(uint page) => advisor.OnAccess(page);

        public void Dispose()
        {
            advisor.Dispose();
            DrainStoppedOnDispose ??= advisor.DrainStopped;
        }
    }

    private static PaprikaPageResidencyAdvisor NewAdvisor(int capacity, int ringCapacity) =>
        new(new PageResidencyTracker(capacity), HistoryDepth, UnitBytes, ringCapacity, LimboLogs.Instance.GetClassLogger<PaprikaPageResidencyAdvisor>());

    private static void AssertDrained(PaprikaPageResidencyAdvisor advisor) =>
        Assert.That(() => advisor.Dispatched + advisor.SkippedRetouched, Is.EqualTo(advisor.Queued).After(TimeoutMs, 10), "every queued unit is released or kept");

    private static Keccak[] Keys(int start, int count) =>
        [.. Enumerable.Range(start, count).Select(index => Keccak.Compute(BitConverter.GetBytes(index)))];

    private static async Task Write(PagedDb db, IEnumerable<Keccak> keys, CommitOptions options)
    {
        using IBatch batch = db.BeginNextBatch();
        foreach (Keccak key in keys) batch.SetRaw(Key.Account(key), key.Span);
        await batch.Commit(options);
    }

    private static int Mismatches(IReadOnlyBatch reader, IEnumerable<Keccak> keys) =>
        keys.Count(key => !Holds(reader, key));

    private static bool Holds(IReadOnlyBatch reader, in Keccak key) =>
        reader.TryGet(Key.Account(key), out ReadOnlySpan<byte> value) && value.SequenceEqual(key.Span);

    private static int ReadWhile(PagedDb db, Keccak[] keys, int seed, Task writer)
    {
        Random random = new(seed);
        int mismatches = 0;
        do
        {
            using IReadOnlyBatch reader = db.BeginReadOnlyBatch();
            for (int i = 0; i < 1000; i++)
            {
                if (!Holds(reader, keys[random.Next(keys.Length)])) mismatches++;
            }
        } while (!writer.IsCompleted);

        return mismatches;
    }

    /// <summary>Reads random pages past the roots from <paramref name="threads"/> threads, checking each holds its number, as <see cref="WriteFile"/> wrote.</summary>
    /// <returns>The distinct pages read, and the reads that found another number.</returns>
    private static (int Touched, int Mismatches) Touch(MemoryMappedPageManager manager, int threads, int accessesPerThread)
    {
        HashSet<uint>[] touched = new HashSet<uint>[threads];
        int mismatches = 0;
        Thread[] readers = [.. Enumerable.Range(0, threads).Select(thread => new Thread(() =>
        {
            Random random = new(thread);
            HashSet<uint> pages = touched[thread] = [];
            for (int i = 0; i < accessesPerThread; i++)
            {
                uint page = (uint)random.Next(HistoryDepth, manager.MaxPage);
                if (BinaryPrimitives.ReadUInt32LittleEndian(manager.GetAt(DbAddress.Page(page)).Span) != page) Interlocked.Increment(ref mismatches);
                pages.Add(page);
            }
        }))];
        foreach (Thread reader in readers) reader.Start();
        foreach (Thread reader in readers) reader.Join();
        return (touched.SelectMany(pages => pages).Distinct().Count(), mismatches);
    }

    /// <summary>Writes a durable <c>paprika.db</c> of <paramref name="size"/> bytes whose every page starts with its number.</summary>
    private string WriteFile(long size)
    {
        Directory.CreateDirectory(_directory.Path);
        byte[] chunk = new byte[Math.Min(size, 1024 * 1024)];
        using SafeFileHandle file = File.OpenHandle(PaprikaFile, FileMode.Create, FileAccess.ReadWrite);
        for (long offset = 0; offset < size; offset += chunk.Length)
        {
            for (int page = 0; page < chunk.Length / Page.PageSize; page++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(chunk.AsSpan(page * Page.PageSize), (uint)(offset / Page.PageSize + page));
            }

            RandomAccess.Write(file, chunk, offset);
        }

        RandomAccess.FlushToDisk(file);
        return PaprikaFile;
    }

    private static void ReadWhole(string path)
    {
        byte[] chunk = new byte[1024 * 1024];
        using SafeFileHandle file = File.OpenHandle(path);
        long offset = 0;
        while (RandomAccess.Read(file, chunk, offset) > 0) offset += chunk.Length;
    }

    private static void DropFromPageCache(string path)
    {
        using SafeFileHandle file = File.OpenHandle(path);
        PosixReclaim.FadviseDontNeed((int)file.DangerousGetHandle(), 0, RandomAccess.GetLength(file));
    }

    /// <summary>Ignores the test where a durable file stays in the page cache once dropped, as on tmpfs, ramfs or an overlay over them.</summary>
    private void IgnoreWherePageCacheCannotBeDropped()
    {
        string probe = Path.Combine(_directory.Path, "probe");
        Directory.CreateDirectory(_directory.Path);
        using (SafeFileHandle file = File.OpenHandle(probe, FileMode.Create, FileAccess.ReadWrite))
        {
            RandomAccess.Write(file, new byte[Environment.SystemPageSize], 0);
            RandomAccess.FlushToDisk(file);
        }

        DropFromPageCache(probe);
        if (CachedPages(probe) != 0) Assert.Ignore("The file system keeps its files in memory, so their page cache cannot be dropped");
    }

    /// <summary>The pages of <paramref name="path"/> in the page cache.</summary>
    private static unsafe int CachedPages(string path)
    {
        using MemoryMappedFile file = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        using MemoryMappedViewAccessor view = file.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
        byte* pointer = null;
        view.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
        try
        {
            return CachedPages(pointer, new FileInfo(path).Length);
        }
        finally
        {
            view.SafeMemoryMappedViewHandle.ReleasePointer();
        }
    }

    /// <summary>The pages of the mapped file range at <paramref name="start"/> in the page cache, as <c>mincore</c> reports them.</summary>
    private static unsafe int CachedPages(byte* start, long length)
    {
        int pageSize = Environment.SystemPageSize;
        byte[] residency = new byte[(length + pageSize - 1) / pageSize];
        fixed (byte* vector = residency)
        {
            Assert.That(Mincore(start, (nuint)length, vector), Is.Zero, $"mincore failed with errno {Marshal.GetLastPInvokeError()}");
        }

        return residency.Count(page => (page & 1) != 0);
    }

    /// <summary>The permissions of the mapping of <paramref name="path"/>, such as <c>rw-s</c>.</summary>
    private static string Permissions(string path) => SmapsEntry(path)[0].Split(' ', StringSplitOptions.RemoveEmptyEntries)[1];

    /// <summary>The resident bytes of the mapping of <paramref name="path"/>.</summary>
    internal static long Rss(string path)
    {
        string rss = SmapsEntry(path).First(line => line.StartsWith("Rss:", StringComparison.Ordinal));
        return long.Parse(rss.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1]) * 1024;
    }

    private static string[] SmapsEntry(string path)
    {
        string[] smaps = File.ReadAllLines("/proc/self/smaps");
        int header = Array.FindIndex(smaps, line => line.EndsWith(path, StringComparison.Ordinal));
        Assert.That(header, Is.Not.Negative, $"{path} is mapped");
        return smaps[header..];
    }

    [LibraryImport("libc", EntryPoint = "madvise", SetLastError = true)]
    private static unsafe partial int Madvise(void* address, nuint length, int advice);

    [LibraryImport("libc", EntryPoint = "mincore", SetLastError = true)]
    private static unsafe partial int Mincore(void* address, nuint length, byte* vector);
}
