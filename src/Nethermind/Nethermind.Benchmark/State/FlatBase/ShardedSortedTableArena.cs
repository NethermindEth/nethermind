// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.IO;
using Nethermind.Benchmarks.State.FlatBase.Sorted;
using Nethermind.State.Flat;
using Nethermind.State.Flat.Io;
using Nethermind.State.Flat.PersistedSnapshots.Storage;

namespace Nethermind.Benchmarks.State.FlatBase;

/// <summary>
/// Prototype "flat base" store: one append-only arena file holding one <see cref="Sorted.SortedTable"/>
/// per key-prefix shard, plus a tiny sidecar directory file mapping shard → table bound. Reads go
/// through the production <see cref="ArenaFile"/> mmap (MADV_RANDOM on Linux); a point read is a
/// two-level table seek — typically one index-block touch and one 4 KiB data-page touch.
/// </summary>
/// <remarks>
/// The shard of a key is its top byte's high bits (<c>key[0] &gt;&gt; (8 − log2(shardCount))</c>), so
/// shards cover contiguous key ranges and writing shards 0..N-1 in order keeps the whole arena
/// ascending. Each table is started on a 4 KiB boundary so the table-relative block alignment holds
/// absolutely. Directory sidecar format: <c>[shardCount i32][offset i64, length i64] × shardCount</c>.
/// </remarks>
internal static class ShardedSortedTableArena
{
    internal const string AccountArenaFile = "account.arena";
    internal const string AccountDirFile = "account.dir";
    internal const string StorageArenaFile = "storage.arena";
    internal const string StorageDirFile = "storage.dir";

    internal static int ShardOf(ReadOnlySpan<byte> key, int shardShift) => key[0] >> shardShift;

    internal static int ShardShift(int shardCount)
    {
        if (shardCount is < 1 or > 256 || (shardCount & (shardCount - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(shardCount), shardCount, "Shard count must be a power of two in [1, 256]");
        return 8 - System.Numerics.BitOperations.Log2((uint)shardCount);
    }
}

/// <summary>Buffered <see cref="IByteBufferWriter"/> over a <see cref="FileStream"/> for streaming
/// <see cref="Sorted.SortedTableBuilder{TWriter}"/> output to disk during dataset build.</summary>
internal sealed class FileByteBufferWriter(FileStream stream) : IByteBufferWriter, IDisposable
{
    private readonly byte[] _buffer = new byte[1 << 20];
    private int _position;

    public long Written { get; private set; }

    // The stream starts at offset 0 of the arena file, which is 4 KiB-aligned by definition.
    public long FirstOffset => 0;

    public Span<byte> GetSpan(int sizeHint)
    {
        if (_position + sizeHint > _buffer.Length) FlushBuffer();
        return _buffer.AsSpan(_position);
    }

    public void Advance(int count)
    {
        _position += count;
        Written += count;
    }

    private void FlushBuffer()
    {
        stream.Write(_buffer, 0, _position);
        _position = 0;
    }

    public void Dispose()
    {
        FlushBuffer();
        stream.Flush();
    }
}

/// <summary>
/// Pointer-backed <see cref="IByteReader{TPin}"/> over the arena's mmap — the production
/// <c>ArenaByteReader</c> minus the reservation/residency tracking, which the benchmark store
/// does not have.
/// </summary>
internal readonly unsafe struct PointerByteReader(byte* basePtr, long length) : IByteReader<NoOpPin>
{
    public long Length => length;

    public bool TryRead(long offset, scoped Span<byte> output)
    {
        if ((ulong)offset + (ulong)output.Length > (ulong)length) return false;
        // Safety: the bounds check above keeps [offset, offset + output.Length) within the mmap
        // region [basePtr, basePtr + length) owned by the ArenaFile the caller keeps alive.
        new ReadOnlySpan<byte>(basePtr + offset, output.Length).CopyTo(output);
        return true;
    }

    public NoOpPin PinBuffer(Bound bound)
    {
        if ((ulong)bound.Offset + (ulong)bound.Length > (ulong)length)
            throw new ArgumentOutOfRangeException(nameof(bound));
        // Safety: same in-bounds guarantee as TryRead; the span never outlives the mmap because the
        // NoOpPin is consumed within the seek call while the backend holds the ArenaFile open.
        return new NoOpPin(new ReadOnlySpan<byte>(basePtr + bound.Offset, checked((int)bound.Length)));
    }
}

/// <summary>Streams one sorted table per shard into an arena file and records the shard directory.
/// Shards must be written in ascending shard order with keys sorted ascending within each shard.</summary>
internal sealed class ShardedArenaWriter : IDisposable
{
    private readonly FileStream _stream;
    private FileByteBufferWriter _writer;
    private readonly Bound[] _tables;
    private readonly string _dirPath;

    public ShardedArenaWriter(string arenaPath, string dirPath, int shardCount)
    {
        _stream = new FileStream(arenaPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 1);
        _writer = new FileByteBufferWriter(_stream);
        _tables = new Bound[shardCount];
        _dirPath = dirPath;
    }

    public void WriteShard(int shard, byte[][] keys, byte[][] values, int count)
    {
        PadToPageBoundary();
        long start = _writer.Written;
        SortedTableBuilder<FileByteBufferWriter> builder = new(ref _writer);
        try
        {
            for (int i = 0; i < count; i++)
                builder.Add(keys[i], values[i]);
            builder.Build();
        }
        finally
        {
            builder.Dispose();
        }

        _tables[shard] = new Bound(start, _writer.Written - start);
    }

    private void PadToPageBoundary()
    {
        long pad = (-_writer.Written) & (PageLayout.PageSize - 1);
        while (pad > 0)
        {
            int chunk = (int)Math.Min(pad, 256);
            _writer.GetSpan(chunk)[..chunk].Clear();
            _writer.Advance(chunk);
            pad -= chunk;
        }
    }

    public void Dispose()
    {
        _writer.Dispose();
        _stream.Dispose();

        byte[] dir = new byte[sizeof(int) + _tables.Length * 2 * sizeof(long)];
        BinaryPrimitives.WriteInt32LittleEndian(dir, _tables.Length);
        for (int i = 0; i < _tables.Length; i++)
        {
            BinaryPrimitives.WriteInt64LittleEndian(dir.AsSpan(sizeof(int) + i * 2 * sizeof(long)), _tables[i].Offset);
            BinaryPrimitives.WriteInt64LittleEndian(dir.AsSpan(sizeof(int) + i * 2 * sizeof(long) + sizeof(long)), _tables[i].Length);
        }

        File.WriteAllBytes(_dirPath, dir);
    }
}

/// <summary>Point-read side of a sharded arena: mmaps the arena via the production
/// <see cref="ArenaFile"/> and seeks the shard's table selected by the key prefix.</summary>
internal sealed unsafe class ShardedArenaReader : IDisposable
{
    private readonly ArenaFile _file;
    private readonly Bound[] _tables;
    private readonly int _shardShift;

    private ShardedArenaReader(ArenaFile file, Bound[] tables)
    {
        _file = file;
        _tables = tables;
        _shardShift = ShardedSortedTableArena.ShardShift(tables.Length);
    }

    public static ShardedArenaReader Open(string arenaPath, string dirPath)
    {
        byte[] dir = File.ReadAllBytes(dirPath);
        int shardCount = BinaryPrimitives.ReadInt32LittleEndian(dir);
        Bound[] tables = new Bound[shardCount];
        for (int i = 0; i < shardCount; i++)
        {
            long offset = BinaryPrimitives.ReadInt64LittleEndian(dir.AsSpan(sizeof(int) + i * 2 * sizeof(long)));
            long length = BinaryPrimitives.ReadInt64LittleEndian(dir.AsSpan(sizeof(int) + i * 2 * sizeof(long) + sizeof(long)));
            tables[i] = new Bound(offset, length);
        }

        ArenaFile file = new(id: 0, arenaPath, new FileInfo(arenaPath).Length);
        // ArenaFile deletes its backing file when the last lease drops unless told otherwise; the
        // benchmark dataset must survive across runs.
        file.PersistOnShutdown();
        return new ShardedArenaReader(file, tables);
    }

    public bool TryGet(ReadOnlySpan<byte> key, Span<byte> valueOut, out int length)
    {
        Bound table = _tables[ShardedSortedTableArena.ShardOf(key, _shardShift)];
        PointerByteReader reader = new(_file.BasePtr, _file.MappedSize);
        if (!SortedTableReader.TrySeek<PointerByteReader, NoOpPin>(in reader, table, key, out Bound value))
        {
            length = 0;
            return false;
        }

        length = (int)value.Length;
        // Safety: TrySeek only returns bounds inside the reader's [0, MappedSize) region.
        new ReadOnlySpan<byte>(_file.BasePtr + value.Offset, length).CopyTo(valueOut);
        return true;
    }

    public void Dispose() => _file.Dispose();
}

/// <summary>The sharded-arena <see cref="IFlatPointReadBackend"/>: one arena for accounts, one for slots.</summary>
internal sealed class SortedArenaBackend : IFlatPointReadBackend
{
    private readonly ShardedArenaReader _accounts;
    private readonly ShardedArenaReader _storage;

    private SortedArenaBackend(ShardedArenaReader accounts, ShardedArenaReader storage)
    {
        _accounts = accounts;
        _storage = storage;
    }

    public static SortedArenaBackend OpenRead(string dir) => new(
        ShardedArenaReader.Open(
            Path.Combine(dir, ShardedSortedTableArena.AccountArenaFile),
            Path.Combine(dir, ShardedSortedTableArena.AccountDirFile)),
        ShardedArenaReader.Open(
            Path.Combine(dir, ShardedSortedTableArena.StorageArenaFile),
            Path.Combine(dir, ShardedSortedTableArena.StorageDirFile)));

    public IFlatReadSession BeginSession() => new Session(this);

    public void Dispose()
    {
        _accounts.Dispose();
        _storage.Dispose();
    }

    private sealed class Session(SortedArenaBackend backend) : IFlatReadSession
    {
        public int GetAccount(ReadOnlySpan<byte> key20, Span<byte> valueOut) =>
            backend._accounts.TryGet(key20, valueOut, out int length) ? length : 0;

        public int GetSlot(ReadOnlySpan<byte> key52, Span<byte> valueOut) =>
            backend._storage.TryGet(key52, valueOut, out int length) ? length : 0;

        public void Dispose() { }
    }
}
