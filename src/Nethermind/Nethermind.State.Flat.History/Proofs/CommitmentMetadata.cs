// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;

namespace Nethermind.State.Flat.History.Proofs;

public sealed class CommitmentMetadata(IColumnsDb<FlatHistoryColumns> history)
{
    public const byte FormatVersion = 1;

    private const byte Marker = 0xFE;
    private static ReadOnlySpan<byte> StampKey => [Marker, 0x01];
    private static ReadOnlySpan<byte> CoverageKey => [Marker, 0x02];
    private static ReadOnlySpan<byte> TipSeriesKey => [Marker, 0x03];

    private readonly IDb _column = history.GetColumnDb(FlatHistoryColumns.AccountCommitments);
    private readonly object _lock = new();

    private readonly HashSet<ValueHash256> _largeStorageTries = [];

    public object WindowWriteLock { get; } = new();

    public bool IsKnownLargeStorageTrie(in ValueHash256 accountPath)
    {
        lock (_largeStorageTries)
        {
            return _largeStorageTries.Contains(accountPath);
        }
    }

    public void RememberLargeStorageTrie(in ValueHash256 accountPath)
    {
        lock (_largeStorageTries)
        {
            _largeStorageTries.Add(accountPath);
        }
    }

    public bool TryReadStamp(CommitmentDepthPolicy policy, out bool matches)
    {
        byte[]? stamp = _column.Get(StampKey);
        if (stamp is null)
        {
            matches = false;
            return false;
        }

        matches = stamp.Length == CommitmentDepthPolicy.StampLength + 1 && stamp[0] == FormatVersion && policy.MatchesStamp(stamp.AsSpan(1));
        return true;
    }

    public void WriteStamp(CommitmentDepthPolicy policy)
    {
        Span<byte> stamp = stackalloc byte[CommitmentDepthPolicy.StampLength + 1];
        stamp[0] = FormatVersion;
        policy.WriteStamp(stamp[1..]);
        lock (_lock)
        {
            _column.PutSpan(StampKey, stamp);
        }
    }

    public bool TryGetCoverage(out ulong fromInclusive, out ulong toInclusive) => TryReadRange(CoverageKey, out fromInclusive, out toInclusive);

    public bool TryGetTipSeries(out ulong startInclusive, out ulong frontierInclusive) => TryReadRange(TipSeriesKey, out startInclusive, out frontierInclusive);

    public bool TryPublishVerifiedCoverage(ulong fromInclusive, ulong toInclusive, out ulong coveredFrom, out ulong coveredTo)
    {
        lock (_lock)
        {
            coveredFrom = fromInclusive;
            coveredTo = toInclusive;
            if (TryReadRange(CoverageKey, out ulong from, out ulong to))
            {
                if (fromInclusive > to + 1 || (to != ulong.MaxValue && toInclusive + 1 < from))
                {
                    coveredFrom = from;
                    coveredTo = to;
                    return false;
                }

                coveredFrom = Math.Min(from, fromInclusive);
                coveredTo = Math.Max(to, toInclusive);
                if (coveredFrom == from && coveredTo == to) return true;
            }

            WriteRange(CoverageKey, coveredFrom, coveredTo);
            return true;
        }
    }

    public void AdvanceTipSeries(ulong firstCaptured, ulong lastCaptured, out bool restarted)
    {
        lock (_lock)
        {
            ulong start = firstCaptured;
            restarted = false;
            if (TryReadRange(TipSeriesKey, out ulong seriesStart, out ulong frontier))
            {
                if (firstCaptured <= frontier + 1) start = seriesStart;
                else restarted = true;
            }

            WriteRange(TipSeriesKey, start, lastCaptured);

            if (start == 0)
            {
                WriteRange(CoverageKey, 0, lastCaptured);
            }
            else if (TryReadRange(CoverageKey, out ulong from, out ulong to) && to + 1 >= start && lastCaptured > to)
            {
                WriteRange(CoverageKey, from, lastCaptured);
            }
        }
    }

    private bool TryReadRange(ReadOnlySpan<byte> key, out ulong first, out ulong last)
    {
        first = 0;
        last = 0;
        byte[]? value = _column.Get(key);
        if (value is not { Length: 2 * sizeof(ulong) }) return false;

        first = BinaryPrimitives.ReadUInt64BigEndian(value);
        last = BinaryPrimitives.ReadUInt64BigEndian(value.AsSpan(sizeof(ulong)));
        return last >= first;
    }

    private void WriteRange(ReadOnlySpan<byte> key, ulong first, ulong last)
    {
        Span<byte> value = stackalloc byte[2 * sizeof(ulong)];
        BinaryPrimitives.WriteUInt64BigEndian(value, first);
        BinaryPrimitives.WriteUInt64BigEndian(value[sizeof(ulong)..], last);
        _column.PutSpan(key, value);
    }
}
