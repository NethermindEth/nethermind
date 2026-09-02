// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using Nethermind.Core;
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

    public void PublishVerifiedCoverage(ulong fromInclusive, ulong toInclusive)
    {
        lock (_lock)
        {
            if (TryReadRange(CoverageKey, out ulong from, out ulong to) && from <= fromInclusive && to >= toInclusive) return;

            WriteRange(CoverageKey, fromInclusive, toInclusive);
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
