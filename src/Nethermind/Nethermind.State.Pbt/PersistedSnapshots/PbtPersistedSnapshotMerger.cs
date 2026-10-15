// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using Nethermind.Core;
using Nethermind.State.Flat;
using Nethermind.State.Flat.Io;
using Nethermind.State.Flat.Persistence.BloomFilter;
using Nethermind.State.Flat.PersistedSnapshots.Sorted;
using Nethermind.State.Flat.PersistedSnapshots.Storage;

namespace Nethermind.State.Pbt.PersistedSnapshots;

public static class PbtPersistedSnapshotMerger
{
    public static void Merge<TWriter>(ReadOnlySpan<PbtPersistedSnapshot> oldestFirst,
        in PbtPersistedMetadata metadata, ref TWriter writer, BloomFilter bloom,
        CancellationToken cancellationToken) where TWriter : IByteBufferWriter
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(bloom);
        if (oldestFirst.IsEmpty || metadata.From != oldestFirst[0].From
            || metadata.To != oldestFirst[^1].To || metadata.TreeRoot != oldestFirst[^1].TreeRoot)
            throw new InvalidOperationException("Persisted PBT merge metadata does not describe its sources.");
        for (int index = 1; index < oldestFirst.Length; index++)
            if (oldestFirst[index - 1].To != oldestFirst[index].From)
                throw new InvalidOperationException("Persisted PBT merge sources are not adjacent.");

        MergeCursor?[] scanners = new MergeCursor[oldestFirst.Length];
        bool[] active = new bool[oldestFirst.Length];
        SortedSet<ushort> owners = [];
        SortedTableBuilder<TWriter> table = new(ref writer);
        try
        {
            for (int index = 0; index < scanners.Length; index++)
            {
                MergeCursor scanner = new(oldestFirst[index]);
                scanners[index] = scanner;
                bool more = scanner.MoveNext();
                while (more && scanner.Key[0] == PbtPersistedKey.Ownership)
                {
                    owners.Add(BinaryPrimitives.ReadUInt16BigEndian(scanner.Key[1..]));
                    more = scanner.MoveNext();
                }
                active[index] = more && PbtPersistedKey.IsEntity(scanner.Key);
            }
            foreach (ushort id in owners) table.Add(PbtPersistedKey.Owner(id), [1]);
            Span<byte> entity = stackalloc byte[255];
            Span<byte> value = stackalloc byte[255];
            Span<byte> address = stackalloc byte[32];
            bool hasAddress = false;
            int clearSource = -1;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int winner = -1;
                for (int index = 0; index < scanners.Length; index++)
                {
                    if (!active[index]) continue;
                    if (winner < 0 || scanners[index]!.Key.SequenceCompareTo(scanners[winner]!.Key) <= 0)
                        winner = index;
                }
                if (winner < 0) break;
                int keyLength = scanners[winner]!.Key.Length;
                scanners[winner]!.Key.CopyTo(entity);
                ReadOnlySpan<byte> key = entity[..keyLength];
                bool emit = true;
                if (key[0] == PbtPersistedKey.Address)
                {
                    if (!hasAddress || !key.Slice(1, 32).SequenceEqual(address))
                    {
                        key.Slice(1, 32).CopyTo(address);
                        hasAddress = true;
                        clearSource = -1;
                    }
                    if (key[33] == PbtPersistedKey.Clear) clearSource = winner;
                    else if (key[33] is PbtPersistedKey.HeaderRun or PbtPersistedKey.StorageRun)
                        emit = winner >= clearSource;
                }
                if (emit) bloom.AddUnsynchronized(PbtPersistedKey.BloomHash(key));

                // A descriptor and its chunks are one revision. Losing sources advance over their complete revision.
                for (int index = 0; index < scanners.Length; index++)
                {
                    MergeCursor? scanner = scanners[index];
                    if (!active[index] || !scanner!.Key.SequenceEqual(key)) continue;
                    bool selected = emit && index == winner;
                    if (selected) CopyRecord(ref table, scanner, value);
                    bool more = scanner.MoveNext();
                    while (more && PbtPersistedKey.IsChunk(scanner.Key))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (selected) CopyRecord(ref table, scanner, value);
                        more = scanner.MoveNext();
                    }
                    active[index] = more && PbtPersistedKey.IsEntity(scanner.Key);
                }
            }
            PbtPersistedFormat.WriteMetadata(ref table, metadata);
            cancellationToken.ThrowIfCancellationRequested();
            table.Build();
            cancellationToken.ThrowIfCancellationRequested();
        }
        finally
        {
            table.Dispose();
            for (int index = 0; index < scanners.Length; index++)
            {
                scanners[index]?.Dispose();
            }
        }
    }

    private static void CopyRecord<TWriter>(ref SortedTableBuilder<TWriter> table, MergeCursor scanner,
        scoped Span<byte> buffer)
        where TWriter : IByteBufferWriter
    {
        int length = checked((int)scanner.Value.Length);
        Span<byte> value = buffer[..length];
        WholeReadSessionReader reader = scanner.CreateReader();
        if (!reader.TryRead(scanner.Value.Offset, value))
            throw new InvalidDataException("Persisted PBT merge value is out of bounds.");
        table.Add(scanner.Key, value);
    }

    private sealed class MergeCursor : IDisposable
    {
        private readonly PbtPersistedSnapshot _snapshot;
        private readonly WholeReadSession _session;
        private SortedTableEnumerator<WholeReadSessionReader, NoOpPin> _cursor;
        private bool _disposed;

        public MergeCursor(PbtPersistedSnapshot snapshot)
        {
            if (!snapshot.TryLease()) throw new ObjectDisposedException(nameof(snapshot));
            _snapshot = snapshot;
            WholeReadSession? session = null;
            try
            {
                session = snapshot.BeginWholeReadSession();
                _session = session;
                WholeReadSessionReader reader = session.CreateReader();
                _cursor = new(reader, new(0, reader.Length));
            }
            catch
            {
                session?.Dispose();
                snapshot.Dispose();
                throw;
            }
        }

        public ReadOnlySpan<byte> Key => _cursor.CurrentKey;
        public Bound Value => _cursor.CurrentValue;
        public WholeReadSessionReader CreateReader() => _session.CreateReader();
        public bool MoveNext() => _cursor.MoveNext(_session.CreateReader());

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _cursor.Dispose();
            _session.Dispose();
            _snapshot.Dispose();
        }
    }

}
