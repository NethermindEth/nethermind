// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.RocksDbBindings;
using Nethermind.State.Flat;
using Nethermind.State.Flat.Persistence;

namespace Nethermind.State.Pbt.Migration;

/// <summary>Owns an immutable, writer-excluding offline preimage-flat and code source.</summary>
internal sealed class MigrationOfflineSourceLease : IDisposable
{
    private readonly List<IDisposable> _owned = [];
    private bool _disposed;

    private MigrationOfflineSourceLease() { }

    public IPersistence.IPersistenceReader Reader { get; private set; } = null!;
    public IReadOnlyKeyValueStore Code { get; private set; } = null!;

    /// <summary>Opens existing Linux source databases without creating or modifying any source files.</summary>
    /// <remarks>The source must be flushed and shut down first. Nonempty WALs are rejected to prevent unbounded recovery memory.</remarks>
    public static MigrationOfflineSourceLease Open(string sourceRoot, ILogManager logManager)
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture is not (Architecture.X64 or Architecture.Arm64))
            throw new PlatformNotSupportedException("Direct offline migration sources require Linux x64/arm64. Use portable snapshot/preimage artifacts on this platform.");

        MigrationOfflineSourceLease lease = new();
        try
        {
            string flatPath = Path.Combine(Path.GetFullPath(sourceRoot), "flat");
            string codePath = Path.Combine(Path.GetFullPath(sourceRoot), "code");
            lease._owned.Add(LockSource(flatPath));
            lease._owned.Add(LockSource(codePath));
            RocksDb flat = lease.OpenDatabase(flatPath);
            RocksDb code = lease.OpenDatabase(codePath);
            ReadOnlyColumn GetColumn(FlatDbColumns column) => new(lease, flat, flat.GetColumnFamily(column.ToString()));
            ReadOnlyColumn metadata = GetColumn(FlatDbColumns.Metadata);
            FlatLayout layout = BasePersistence.ReadLayout(metadata) ?? throw new InvalidDataException("Offline source has no recorded flat layout.");
            if (layout is not (FlatLayout.PreimageFlat or FlatLayout.PreimageFlatV1))
                throw new InvalidDataException($"Offline source layout is {layout}, not preimage-flat.");
            ReadOnlyColumn storage = GetColumn(FlatDbColumns.Storage);
            bool rlpWrapSlots = BasePersistence.ReadSlotEncoding(metadata) switch
            {
                1 => true,
                0 => false,
                null => storage.FirstKey is null,
                _ => throw new InvalidDataException("Offline source has an unknown slot encoding.")
            };
            if (!rlpWrapSlots && logManager.GetClassLogger<MigrationOfflineSourceLease>() is { IsWarn: true } logger)
                logger.Warn("Offline migration source uses legacy raw storage slot encoding.");
            Flat.StateId state = BasePersistence.ReadCurrentState(metadata);
            if (state.BlockNumber == ulong.MaxValue)
                throw new InvalidDataException("Offline source has no committed current state.");
            BaseTriePersistence.Reader trieReader = new(GetColumn(FlatDbColumns.StateTopNodes), GetColumn(FlatDbColumns.StateNodes),
                GetColumn(FlatDbColumns.StorageNodes), GetColumn(FlatDbColumns.FallbackNodes));
            PreimageRocksdbPersistence.FakeHashFlatReader<BaseFlatPersistence.Reader> flatReader = new(new BaseFlatPersistence.Reader(
                GetColumn(FlatDbColumns.Account), storage, isPreimageMode: true, rlpWrapSlots: rlpWrapSlots,
                fullAddressStorageKey: layout == FlatLayout.PreimageFlat));
            lease.Reader = new BasePersistence.Reader<PreimageRocksdbPersistence.FakeHashFlatReader<BaseFlatPersistence.Reader>, BaseTriePersistence.Reader>(
                flatReader, trieReader, state, lease);
            lease.Code = new ReadOnlyColumn(lease, code, code.GetDefaultColumnFamily());
            return lease;
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    private RocksDb OpenDatabase(string path)
    {
        foreach (string wal in Directory.EnumerateFiles(path, "*.log"))
            if (new FileInfo(wal).Length != 0)
                throw new InvalidDataException($"Offline source contains a nonempty WAL: {wal}. Flush and close the source before migration.");

        DbOptions options = new();
        _owned.Add(options);
        options.SetCreateIfMissing(false);
        options.SetCreateMissingColumnFamilies(false);
        options.SetMaxOpenFiles(128);
        ColumnFamilyOptions columnOptions = new();
        _owned.Add(columnOptions);
        ColumnFamilies columns = new(columnOptions);
        foreach (string name in RocksDb.ListColumnFamilies(options, path))
            columns.Add(name, columnOptions);
        RocksDb database = RocksDb.OpenReadOnly(options, path, columns, false);
        _owned.Add(database);
        return database;
    }

    private static SafeFileHandle LockSource(string path)
    {
        SafeFileHandle handle = File.OpenHandle(Path.Combine(path, "LOCK"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        try
        {
            // OFD locks conflict with RocksDB's POSIX write lock even within this process and survive unrelated fd closes.
            FileLock fileLock = new();
            if (Fcntl(handle, 37 /* F_OFD_SETLK */, ref fileLock) != 0 || Flock(handle, 6 /* LOCK_EX | LOCK_NB */) != 0)
                throw new IOException($"Offline source is in use or cannot be locked: {path} (errno {Marshal.GetLastPInvokeError()}).");
            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileLock
    {
        public short Type; // F_RDLCK = 0, valid on an O_RDONLY descriptor.
        public short Whence;
        public long Start;
        public long Length;
        public int ProcessId;
    }

    [DllImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    private static extern int Fcntl(SafeFileHandle descriptor, int command, ref FileLock fileLock);

    [DllImport("libc", EntryPoint = "flock", SetLastError = true)]
    private static extern int Flock(SafeFileHandle descriptor, int operation);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        for (int i = _owned.Count - 1; i >= 0; i--) _owned[i].Dispose();
        _owned.Clear();
    }

    private sealed class ReadOnlyColumn(MigrationOfflineSourceLease owner, RocksDb database, IColumnFamilyHandle column) : ISortedKeyValueStore
    {
        public byte[]? Get(ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None)
        {
            ObjectDisposedException.ThrowIf(owner._disposed, owner);
            return database.Get(key, column);
        }

        public byte[]? FirstKey => GetBoundary(false);
        public byte[]? LastKey => GetBoundary(true);

        private byte[]? GetBoundary(bool last)
        {
            ObjectDisposedException.ThrowIf(owner._disposed, owner);
            using Iterator iterator = database.NewIterator(column);
            if (last) iterator.SeekToLast(); else iterator.SeekToFirst();
            iterator.ThrowIfError();
            return iterator.Valid() ? iterator.Key() : null;
        }

        public ISortedView GetViewBetween(ReadOnlySpan<byte> firstKeyInclusive, ReadOnlySpan<byte> lastKeyExclusive, ReadFlags flags = ReadFlags.None)
        {
            ObjectDisposedException.ThrowIf(owner._disposed, owner);
            ReadOptions options = new();
            try
            {
                options.SetFillCache(false);
                options.SetTotalOrderSeek(true);
                if (!firstKeyInclusive.IsEmpty) options.SetIterateLowerBound(firstKeyInclusive);
                if (!lastKeyExclusive.IsEmpty) options.SetIterateUpperBound(lastKeyExclusive);
                return new SortedView(database.NewIterator(column, options), options);
            }
            catch
            {
                options.Dispose();
                throw;
            }
        }
    }

    private sealed class SortedView(Iterator iterator, ReadOptions options) : ISortedView
    {
        private bool _started;
        public bool StartBefore(ReadOnlySpan<byte> value)
        {
            if (_started) throw new InvalidOperationException("Cannot seek after iteration started.");
            iterator.SeekForPrev(value);
            iterator.ThrowIfError();
            return _started = iterator.Valid();
        }
        public bool MoveNext()
        {
            if (_started) iterator.Next(); else iterator.SeekToFirst();
            _started = true;
            iterator.ThrowIfError();
            return iterator.Valid();
        }
        public ReadOnlySpan<byte> CurrentKey => iterator.GetKeySpan();
        public ReadOnlySpan<byte> CurrentValue => iterator.GetValueSpan();
        public void Dispose()
        {
            iterator.Dispose();
            options.Dispose();
        }
    }
}
