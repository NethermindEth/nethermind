// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System.Collections.Concurrent;

namespace Nethermind.Pbt.NodeBuffer;

/// <summary>The records of the groups a traversal loads from an <see cref="IPbtStore"/>, readable until <see cref="Clear"/>.</summary>
/// <remarks>
/// Records are written to pooled chunks that never move, so a record handed out stays valid as more are added. Buffers are pooled;
/// a returned one releases its records.
/// </remarks>
public sealed class PbtLoadedRecordBuffer : IDisposable
{
    private const int ChunkBytes = 64 * 1024;
    private const int MaxPooled = 64;

    private static readonly ConcurrentQueue<PbtLoadedRecordBuffer> Pool = new();
    private static readonly ReadOnlySpanAction<byte, Target> Write = static (group, target) => target.Record = target.Buffer.Add(group, target.Path);

    private readonly List<byte[]> _chunks = [];
    private int _used = ChunkBytes;

    public static PbtLoadedRecordBuffer Rent() => Pool.TryDequeue(out PbtLoadedRecordBuffer? buffer) ? buffer : new PbtLoadedRecordBuffer();

    /// <summary>Releases the records of <paramref name="buffer"/>, which must no longer be read, and pools it.</summary>
    public static void Return(PbtLoadedRecordBuffer buffer)
    {
        buffer.Clear();
        if (Pool.Count < MaxPooled) Pool.Enqueue(buffer);
    }

    /// <summary>Loads the group at <paramref name="path"/> from <paramref name="store"/> into this buffer.</summary>
    /// <returns>False when the store has no such group.</returns>
    public bool TryLoad(IPbtStore store, scoped in PbtTraversalPath path, out PbtGroupRecord record)
    {
        record = default;
        PbtStorageNodePath groupPath = path.ToPath<PbtStorageNodePath>();
        return store.TryLoad(path, Write, new Target(this, in groupPath, ref record));
    }

    /// <summary>Releases every record, which must no longer be read.</summary>
    public void Clear()
    {
        foreach (byte[] chunk in _chunks) ArrayPool<byte>.Shared.Return(chunk);
        _chunks.Clear();
        _used = ChunkBytes;
    }

    public void Dispose() => Clear();

    /// <summary>Copies a record, or writes the record of a payload as the group at <paramref name="path"/>, with no links.</summary>
    private PbtGroupRecord Add(ReadOnlySpan<byte> group, in PbtStorageNodePath path)
    {
        if (PbtGroupRecordCodec.IsRecord(group))
        {
            group.CopyTo(Reserve(group.Length, out PbtGroupRecord copied));
            return copied;
        }

        group.CopyTo(PbtGroupRecordCodec.WriteGroup(Reserve(PbtGroupRecordCodec.Length(group.Length), out PbtGroupRecord record), path, default, group.Length));
        return record;
    }

    private Span<byte> Reserve(int length, out PbtGroupRecord record)
    {
        if (length > ChunkBytes - _used)
        {
            _chunks.Add(ArrayPool<byte>.Shared.Rent(Math.Max(ChunkBytes, length)));
            _used = 0;
        }

        byte[] chunk = _chunks[^1];
        record = new PbtGroupRecord(chunk, _used);
        Span<byte> destination = chunk.AsSpan(_used, length);
        _used += length;
        return destination;
    }

    private readonly ref struct Target
    {
        public readonly PbtLoadedRecordBuffer Buffer;
        public readonly ref readonly PbtStorageNodePath Path;
        public readonly ref PbtGroupRecord Record;

        public Target(PbtLoadedRecordBuffer buffer, ref readonly PbtStorageNodePath path, ref PbtGroupRecord record)
        {
            Buffer = buffer;
            Path = ref path;
            Record = ref record;
        }
    }
}
