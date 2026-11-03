// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Nethermind.Core.Extensions;
using NUnit.Framework;
using Paprika.Crypto;
using Paprika.Data;
using Paprika.Store;
using Paprika.Store.PageManagers;

namespace Paprika.Tests.Store;

public partial class MemoryMappedPageManagerTests
{
    private const long Mb = 1024 * 1024;
    private const byte HistoryDepth = 2;
    private const int Seed = 19;

    private static readonly byte[] Value = Bytes.FromHexString("0x0102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f20");

    [Test]
    public void Second_open_of_the_same_directory_throws()
    {
        using TempDirectory dir = new();
        PagedDb first = Open(dir, Mb);
        Assert.That(() => Open(dir, Mb), Throws.InstanceOf<IOException>());
        first.Dispose();

        // A store that fails to open releases its file, and its mapping, which only Linux lists.
        FailingObserver failing = new();
        Assert.That(() => PagedDb.MemoryMappedDb(Mb, HistoryDepth + 1, dir.Path, accessObserver: null), Throws.InvalidOperationException);
        Assert.That(() => PagedDb.MemoryMappedDb(Mb, 1, dir.Path, accessObserver: null), Throws.ArgumentException);
        Assert.That(() => PagedDb.MemoryMappedDb(Mb, HistoryDepth, dir.Path, failing), Throws.InvalidOperationException.With.Message.EqualTo(nameof(FailingObserver)));
        if (OperatingSystem.IsLinux()) Assert.That(IsMapped(failing.BasePointer), Is.False);
        Assert.That(() => Open(dir, Mb).Dispose(), Throws.Nothing);
    }

    [Test]
    public void Dispose_is_idempotent()
    {
        using TempDirectory dir = new();
        MemoryMappedPageManager manager = new(Mb, HistoryDepth, dir.Path, accessObserver: null);
        manager.Dispose();

        Assert.That(manager.Dispose, Throws.Nothing);
    }

    [Test]
    public void Mapping_is_shared_and_advised_random()
    {
        if (!OperatingSystem.IsLinux()) Assert.Ignore("/proc/self/smaps and MADV_RANDOM are Linux only");

        using TempDirectory dir = new();
        using PagedDb db = Open(dir, Mb);

        string[] smaps = File.ReadAllLines("/proc/self/smaps");
        string mappedFile = Path.Combine(Path.GetFileName(dir.Path), Path.GetFileName(MemoryMappedPageManager.GetPaprikaFilePath(dir.Path)));
        int mapping = Array.FindIndex(smaps, line => line.EndsWith(mappedFile, StringComparison.Ordinal));
        Assert.That(mapping, Is.Not.Negative, "The file should be mapped");

        string permissions = smaps[mapping].Split(' ', StringSplitOptions.RemoveEmptyEntries)[1];
        string[] vmFlags = smaps.Skip(mapping + 1).First(line => line.StartsWith("VmFlags:", StringComparison.Ordinal)).Split(' ');
        using (Assert.EnterMultipleScope())
        {
            Assert.That(permissions, Is.EqualTo("rw-s"));
            Assert.That(vmFlags, Does.Contain("rr"), "MADV_RANDOM");
        }
    }

    [Test]
    public void Growth_reserves_disk_blocks_before_pages_are_written()
    {
        const long size = 16 * Mb;
        if (!OperatingSystem.IsLinux()) Assert.Ignore("fallocate is Linux only");

        using TempDirectory dir = new();
        using (SafeFileHandle probe = File.OpenHandle(Path.Combine(dir.Path, "probe"), FileMode.CreateNew, FileAccess.ReadWrite))
        {
            if (Fallocate((int)probe.DangerousGetHandle(), 0, 0, Page.PageSize) != 0) Assert.Ignore("The file system does not support fallocate");
        }

        using PagedDb db = Open(dir, size);
        using IBatch batch = db.BeginNextBatch();
        batch.SetAccount(Values.Key0, Value);

        // The first new page reserves the rest of the file, which is smaller than a preallocation chunk.
        Assert.That(AllocatedBytes(MemoryMappedPageManager.GetPaprikaFilePath(dir.Path)), Is.GreaterThanOrEqualTo(size - HistoryDepth * Page.PageSize));
    }

    [Test]
    public async Task Existing_file_larger_than_configured_size_throws()
    {
        using TempDirectory dir = new();
        using (PagedDb db = Open(dir, 2 * Mb))
        {
            await db.SetAccountAndCommit(Value);
        }

        Assert.That(() => Open(dir, Mb), Throws.InstanceOf<ArgumentOutOfRangeException>());

        using PagedDb grown = Open(dir, 4 * Mb);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(new FileInfo(MemoryMappedPageManager.GetPaprikaFilePath(dir.Path)).Length, Is.EqualTo(4 * Mb));
            Assert.That(grown.ReadAccount(), Is.EqualTo(Value));
        }
    }

    [Test]
    public async Task Reopen_reads_back_committed_state()
    {
        using TempDirectory dir = new();
        Random random = new(Seed);
        Keccak[] accounts = random.NextKeccaks(2_000);
        Keccak contract = random.NextKeccak();

        using (PagedDb db = Open(dir, 64 * Mb))
        {
            for (int round = 0; round < 3; round++)
            {
                using IBatch batch = db.BeginNextBatch();
                foreach (Keccak account in accounts)
                {
                    batch.SetAccount(account, ValueAt(account, round));
                    batch.SetStorage(contract, account, ValueAt(account, round));
                }

                await batch.Commit(CommitOptions.FlushDataAndRoot);
            }
        }

        using PagedDb reopened = Open(dir, 64 * Mb);
        using IReadOnlyBatch read = reopened.BeginReadOnlyBatch();
        read.VerifyNoPagesMissing();
        int matching = accounts.Count(account =>
            read.TryGet(Key.Account(account), out ReadOnlySpan<byte> stored) && stored.SequenceEqual(ValueAt(account, 2)) &&
            read.TryGet(Key.StorageCell(NibblePath.FromKey(contract), account), out ReadOnlySpan<byte> slot) && slot.SequenceEqual(ValueAt(account, 2)));
        Assert.That(matching, Is.EqualTo(accounts.Length));

        static byte[] ValueAt(in Keccak account, int round)
        {
            byte[] value = account.BytesAsSpan.ToArray();
            BinaryPrimitives.WriteInt32BigEndian(value, round);
            return value;
        }
    }

    [Test]
    public async Task Access_observer_sees_every_access_and_is_disposed_before_unmap()
    {
        using TempDirectory dir = new();
        RecordingObserver observer = new();
        // MMapOnly writes nothing to the file, so every access goes through the decorator.
        MemoryMappedPageManager manager = new(Mb, HistoryDepth, dir.Path, observer, PersistenceOptions.MMapOnly);
        TestPageManager pages = new(manager);
        using (PagedDb db = new(pages, HistoryDepth))
        {
            await db.SetAccountAndCommit(Value);
            Assert.That(db.ReadAccount(), Is.EqualTo(Value));
        }

        manager.Dispose();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(observer.Calls, Is.EqualTo((string[])[$"OnMapped {Mb}", .. pages.AccessedPages.Select(page => $"OnAccess {page}"), "Dispose"]));
            Assert.That(pages.AccessedPages, Does.Contain(0u).And.Contain(1u).And.Contain((uint)HistoryDepth), "Root pages are reported too");
        }
    }

    [Test]
    public async Task Write_run_reports_every_page()
    {
        using TempDirectory dir = new();
        RecordingObserver observer = new();
        using (MemoryMappedPageManager manager = new(Mb, HistoryDepth, dir.Path, observer))
        {
            await manager.WritePages([new DbAddress(9), new DbAddress(5), new DbAddress(6), new DbAddress(7)], CommitOptions.FlushDataOnly);
        }

        Assert.That(observer.Calls, Is.EqualTo((string[])[$"OnMapped {Mb}", "OnAccess 5", "OnAccess 6", "OnAccess 7", "OnAccess 9", "Dispose"]));
    }

    private static PagedDb Open(TempDirectory dir, long size) => PagedDb.MemoryMappedDb(size, HistoryDepth, dir.Path, accessObserver: null);

    /// <summary>Records the calls it receives, in order.</summary>
    private sealed class RecordingObserver : IPageAccessObserver
    {
        private readonly ConcurrentQueue<string> _calls = new();
        private nint _basePointer;

        public string[] Calls => [.. _calls];

        public void OnMapped(nint basePointer, long mappedBytes)
        {
            _basePointer = basePointer;
            _calls.Enqueue($"OnMapped {mappedBytes}");
        }

        public void OnAccess(uint page) => _calls.Enqueue($"OnAccess {page}");

        // Only Linux lists the mappings, in /proc/self/maps.
        public void Dispose() => _calls.Enqueue(!OperatingSystem.IsLinux() || IsMapped(_basePointer) ? "Dispose" : "Dispose after unmap");
    }

    /// <summary>Fails to start once the mapping is reported.</summary>
    private sealed class FailingObserver : IPageAccessObserver
    {
        public nint BasePointer { get; private set; }

        public void OnMapped(nint basePointer, long mappedBytes)
        {
            BasePointer = basePointer;
            throw new InvalidOperationException(nameof(FailingObserver));
        }

        public void OnAccess(uint page) { }

        public void Dispose() { }
    }

    private static bool IsMapped(nint address) =>
        File.ReadLines("/proc/self/maps").Any(line => line.StartsWith($"{address:x}-", StringComparison.Ordinal));

    private static unsafe long AllocatedBytes(string path)
    {
        const int atFdCwd = -100;
        const uint statxBlocks = 0x400;
        const int blocksOffset = 48;
        const int blockSize = 512;

        byte* statx = stackalloc byte[256];
        Assert.That(Statx(atFdCwd, path, 0, statxBlocks, statx), Is.Zero, $"statx failed with errno {Marshal.GetLastPInvokeError()}");
        return (long)*(ulong*)(statx + blocksOffset) * blockSize;
    }

    [LibraryImport("libc", EntryPoint = "fallocate", SetLastError = true)]
    private static partial int Fallocate(int fd, int mode, long offset, long length);

    [LibraryImport("libc", EntryPoint = "statx", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static unsafe partial int Statx(int directoryFd, string path, int flags, uint mask, byte* buffer);
}
