// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using Nethermind.Core.Crypto;

namespace Nethermind.State.Flat.History.Walk;

internal sealed class MismatchSink
{
    public const int MaxRecorded = 100_000;

    private readonly List<HistoryWalkMismatch> _mismatches = [];

    public void Add(in HistoryWalkMismatch mismatch)
    {
        lock (_mismatches)
        {
            if (_mismatches.Count < MaxRecorded) _mismatches.Add(mismatch);
        }
    }

    public void AddRange(List<HistoryWalkMismatch> mismatches)
    {
        lock (_mismatches)
        {
            int room = MaxRecorded - _mismatches.Count;
            if (room <= 0) return;

            _mismatches.AddRange(mismatches.Count <= room ? mismatches : mismatches.GetRange(0, room));
        }
    }

    public void AddRange(MismatchSink other)
    {
        lock (other._mismatches)
        {
            AddRange(other._mismatches);
        }
    }

    public List<HistoryWalkMismatch> Drain()
    {
        lock (_mismatches)
        {
            List<HistoryWalkMismatch> sorted = [.. _mismatches];
            sorted.Sort(static (a, b) => a.Block.CompareTo(b.Block));
            return sorted;
        }
    }

    public byte[] Encode(List<HistoryWalkMismatch>? pending = null)
    {
        lock (_mismatches)
        {
            int count = _mismatches.Count + (pending?.Count ?? 0);
            byte[] encoded = new byte[count * RecordLength];
            int offset = 0;
            foreach (HistoryWalkMismatch mismatch in _mismatches) Write(encoded.AsSpan(offset, RecordLength), mismatch, ref offset);
            if (pending is not null) foreach (HistoryWalkMismatch mismatch in pending) Write(encoded.AsSpan(offset, RecordLength), mismatch, ref offset);
            return encoded;
        }
    }

    public void Decode(ReadOnlySpan<byte> encoded)
    {
        lock (_mismatches)
        {
            for (; encoded.Length >= RecordLength && _mismatches.Count < MaxRecorded; encoded = encoded[RecordLength..])
            {
                _mismatches.Add(new HistoryWalkMismatch(
                    BinaryPrimitives.ReadUInt64BigEndian(encoded),
                    (HistoryWalkMismatchKind)encoded[sizeof(ulong)],
                    new ValueHash256(encoded.Slice(sizeof(ulong) + 1, Hash256.Size)),
                    new ValueHash256(encoded.Slice(sizeof(ulong) + 1 + Hash256.Size, Hash256.Size))));
            }
        }
    }

    private const int RecordLength = sizeof(ulong) + 1 + Hash256.Size + Hash256.Size;

    private static void Write(Span<byte> destination, in HistoryWalkMismatch mismatch, ref int offset)
    {
        BinaryPrimitives.WriteUInt64BigEndian(destination, mismatch.Block);
        destination[sizeof(ulong)] = (byte)mismatch.Kind;
        mismatch.Rebuilt.Bytes.CopyTo(destination[(sizeof(ulong) + 1)..]);
        mismatch.Expected.Bytes.CopyTo(destination[(sizeof(ulong) + 1 + Hash256.Size)..]);
        offset += RecordLength;
    }
}
