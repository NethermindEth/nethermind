// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Core.Exceptions;
using Nethermind.Db;
using Nethermind.Logging;

namespace Nethermind.State.Flat.History.Proofs;

public sealed class CommitmentMetadata(IColumnsDb<FlatHistoryColumns> history, CommitmentDepthPolicy policy)
{
    public const byte FormatVersion = 2;

    private const byte Marker = 0xFE;
    private static ReadOnlySpan<byte> StampKey => [Marker, 0x01];
    private static ReadOnlySpan<byte> CoverageKey => [Marker, 0x02];
    private static ReadOnlySpan<byte> TipSeriesKey => [Marker, 0x03];
    private static ReadOnlySpan<byte> WalkRangeKey => [Marker, 0x04];
    private static ReadOnlySpan<byte> RetainedFromEpochKey => [Marker, 0x07];
    private static ReadOnlySpan<byte> FineFromEpochKey => [Marker, 0x08];
    private const byte WalkItemMarker = 0x05;
    private const byte WalkItemProgressMarker = 0x06;
    public const int MaxWalkItems = 1 << 16;

    private const int StorageTrieDepthCacheEntries = 1 << 16;

    private readonly IDb _column = history.GetColumnDb(FlatHistoryColumns.AccountCommitments);
    private readonly IDb _storageColumn = history.GetColumnDb(FlatHistoryColumns.StorageCommitments);
    private readonly CommitmentStore _storages = new(history.GetColumnDb(FlatHistoryColumns.StorageCommitments), policy, CommitmentKeyLayout.IdentityLength);
    private readonly object _lock = new();
    private readonly object _depthWriteLock = new();
    private readonly ClockCache<ValueHash256, int> _storageTrieDepths = new(StorageTrieDepthCacheEntries);
    private bool _layoutEnsured;

    public object WindowWriteLock { get; } = new();

    public int StorageTrieDepth(in ValueHash256 accountPath)
    {
        if (_storageTrieDepths.TryGet(accountPath, out int depth)) return depth;

        depth = _storages.ReadStorageTrieDepth(accountPath);
        _storageTrieDepths.Set(accountPath, depth);
        return depth;
    }

    public int NoteStorageTrieDepth(in ValueHash256 accountPath, int depth)
    {
        int known = StorageTrieDepth(accountPath);
        if (depth <= known) return known;

        lock (_depthWriteLock)
        {
            known = StorageTrieDepth(accountPath);
            if (depth <= known) return known;

            _storages.WriteStorageTrieDepth(accountPath, depth);
            _storageTrieDepths.Set(accountPath, depth);
            return depth;
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

    public void EnsureLayout(CommitmentDepthPolicy policy, bool discardMismatched, ILogger logger)
    {
        lock (_lock)
        {
            if (_layoutEnsured) return;

            if (TryReadStamp(policy, out bool matches) && !matches)
            {
                if (!discardMismatched)
                {
                    throw new InvalidConfigurationException(
                        "The archive proof commitment columns were written under a different layout than this node is " +
                        $"configured for ({policy}). Rows from the two layouts cannot be read together: set " +
                        "FlatDb.ArchiveProofDiscardMismatchedLayout to delete them and rebuild, or restore the previous FlatDb.ArchiveProof settings.", -1);
                }

                DiscardAll();
                if (logger.IsWarn) logger.Warn(
                    $"Archive proof commitment columns written under a different layout were deleted (FlatDb.ArchiveProofDiscardMismatchedLayout); they rebuild from scratch under {policy}.");
            }

            WriteStamp(policy);
            _layoutEnsured = true;
        }
    }

    private void DiscardAll()
    {
        ReadOnlySpan<byte> first = [0x00];
        Span<byte> last = stackalloc byte[CommitmentKeyLayout.MaxKeyLength + 1];
        last.Fill(0xFF);
        Discard(_column, first, last);
        Discard(_storageColumn, first, last);
        _storageTrieDepths.Clear();
    }

    private static void Discard(IDb column, ReadOnlySpan<byte> first, ReadOnlySpan<byte> last)
    {
        IRangeRemovableKeyValueStore removable = (IRangeRemovableKeyValueStore)column;
        removable.RemoveRange(first, last);
        removable.ReclaimRange(first, last);
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

    public bool TryGetCoverage(out ulong fromInclusive, out ulong toInclusive)
    {
        if (!TryReadRange(CoverageKey, out fromInclusive, out toInclusive)) return false;

        ulong floor = policy.EpochStart(RetainedFromEpoch);
        if (floor > fromInclusive) fromInclusive = floor;
        return fromInclusive <= toInclusive;
    }

    public ulong RetainedFromEpoch
    {
        get
        {
            byte[]? value = _column.Get(RetainedFromEpochKey);
            return value is { Length: sizeof(ulong) } ? BinaryPrimitives.ReadUInt64BigEndian(value) : 0;
        }
    }

    public void SetRetainedFromEpoch(ulong epoch) => WriteEpoch(RetainedFromEpochKey, epoch);

    public ulong FineFromEpoch
    {
        get
        {
            byte[]? value = _column.Get(FineFromEpochKey);
            return value is { Length: sizeof(ulong) } ? BinaryPrimitives.ReadUInt64BigEndian(value) : 0;
        }
    }

    public void SetFineFromEpoch(ulong epoch) => WriteEpoch(FineFromEpochKey, epoch);

    private void WriteEpoch(ReadOnlySpan<byte> key, ulong epoch)
    {
        Span<byte> value = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64BigEndian(value, epoch);
        lock (_lock)
        {
            _column.PutSpan(key, value);
        }
    }

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

    public bool TryGetWalkInProgress(out ulong fromInclusive, out ulong toInclusive) => TryReadRange(WalkRangeKey, out fromInclusive, out toInclusive);

    public void BeginWalk(ulong fromInclusive, ulong toInclusive, int items)
    {
        lock (_lock)
        {
            if (TryReadRange(WalkRangeKey, out ulong from, out ulong to) && from == fromInclusive && to == toInclusive) return;

            ClearWalkItems(items);
            WriteRange(WalkRangeKey, fromInclusive, toInclusive);
        }
    }

    public bool IsWalkItemDone(int item)
    {
        Span<byte> key = stackalloc byte[4];
        WriteWalkItemKey(key, item);
        return _column.KeyExists(key);
    }

    public void MarkWalkItemDone(int item, ReadOnlySpan<byte> mismatches)
    {
        Span<byte> key = stackalloc byte[4];
        WriteWalkItemKey(key, item);
        Span<byte> progressKey = stackalloc byte[4];
        WriteWalkItemKey(progressKey, item, WalkItemProgressMarker);
        byte[] value = new byte[1 + mismatches.Length];
        value[0] = 1;
        mismatches.CopyTo(value.AsSpan(1));
        lock (_lock)
        {
            _column.PutSpan(key, value);
            _column.Remove(progressKey);
        }
    }

    public ReadOnlySpan<byte> WalkItemMismatches(int item)
    {
        Span<byte> key = stackalloc byte[4];
        WriteWalkItemKey(key, item);
        byte[]? value = _column.Get(key);
        return value is { Length: > 1 } ? value.AsSpan(1) : [];
    }

    public bool TryGetWalkItemProgress(int item, out ulong progress) => TryGetWalkItemProgress(item, out progress, out _);

    public bool TryGetWalkItemProgress(int item, out ulong progress, out ReadOnlySpan<byte> mismatches)
    {
        Span<byte> key = stackalloc byte[4];
        WriteWalkItemKey(key, item, WalkItemProgressMarker);
        byte[]? value = _column.Get(key);
        if (value is not { Length: >= sizeof(ulong) })
        {
            progress = 0;
            mismatches = [];
            return false;
        }

        progress = BinaryPrimitives.ReadUInt64BigEndian(value);
        mismatches = value.AsSpan(sizeof(ulong));
        return true;
    }

    public void MarkWalkItemProgress(int item, ulong progress, ReadOnlySpan<byte> mismatches)
    {
        Span<byte> key = stackalloc byte[4];
        WriteWalkItemKey(key, item, WalkItemProgressMarker);
        byte[] value = new byte[sizeof(ulong) + mismatches.Length];
        BinaryPrimitives.WriteUInt64BigEndian(value, progress);
        mismatches.CopyTo(value.AsSpan(sizeof(ulong)));
        lock (_lock)
        {
            _column.PutSpan(key, value);
        }
    }

    public void ClearWalk(int items)
    {
        lock (_lock)
        {
            ClearWalkItems(items);
            _column.Remove(WalkRangeKey);
        }
    }

    private void ClearWalkItems(int items)
    {
        Span<byte> key = stackalloc byte[4];
        for (int item = 0; item < items; item++)
        {
            WriteWalkItemKey(key, item);
            _column.Remove(key);
            WriteWalkItemKey(key, item, WalkItemProgressMarker);
            _column.Remove(key);
        }
    }

    private static void WriteWalkItemKey(Span<byte> key, int item, byte marker = WalkItemMarker)
    {
        if (item < 0 || item >= MaxWalkItems) throw new ArgumentOutOfRangeException(nameof(item));

        key[0] = Marker;
        key[1] = marker;
        BinaryPrimitives.WriteUInt16BigEndian(key[2..], (ushort)item);
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
