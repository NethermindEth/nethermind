// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Diagnostics;
using System.Collections.ObjectModel;
using Nethermind.Core;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Crypto;
using Nethermind.Db;

namespace Nethermind.State.Flat.History;

/// <summary>
/// The <c>AvailableBlocks</c> column: per-block markers (<c>[block BE] -> captured state root</c>) plus a
/// contiguous-from-genesis watermark and a format version. A read at H needs <c>H &lt;= watermark</c> and a matching
/// state root, so a gap cannot be shadowed and a non-canonical EIP-1898 hash is rejected.
/// </summary>
public sealed class HistoryAvailability
{
    // Data stamped 3 or below keys accounts by a truncated path and is unreadable under these seeks.
    internal const byte FormatVersion = 4;

    // Stamped when a retention floor is first published, so floor-unaware binaries refuse the DB outright.
    internal const byte WindowedFormatVersion = 5;

    private const int BlockBytes = sizeof(ulong);
    private const int RootBytes = 32;

    // Reserved keys, deliberately not BlockBytes long so they can never collide with a per-block marker key.
    private static ReadOnlySpan<byte> WatermarkKey => "history:watermark"u8;
    private static ReadOnlySpan<byte> FormatVersionKey => "history:format"u8;

    private static ReadOnlySpan<byte> GlobalFloorKey => "history:floor:global"u8;

    // Point scopes only: one record per address. A range scope would be a new kind under a different prefix.
    private static ReadOnlySpan<byte> ScopeRecordPrefix => "history:floor:scope:"u8;
    private const int ScopeRecordPrefixLength = 20; // == ScopeRecordPrefix.Length, asserted in the static ctor
    private const int ScopeRecordKeyLength = ScopeRecordPrefixLength + HistoryKeyLayout.ScopeKeyLength;
    private const int ScopeRecordValueLength = BlockBytes;

    private readonly IDb _availableBlocks;
    private readonly ISortedKeyValueStore _sortedAvailableBlocks;
    private readonly Lock _floorLock = new();

    // Monotonic fast path: never used to refuse (a miss re-reads), so a reader instance cannot lag the writer.
    private long _observedWatermark = -1;

    // Refusing early is safe because the floor only moves up; an accept always re-reads the DB.
    private long _observedFloor = -1;

    // Bumped once a capture batch is durable and always before the persist that supersedes the live column, so a
    // reader that sees it unchanged across its live read knows no row can have appeared under it.
    private long _captureGeneration;

    // Generation-guarded: a scan only publishes if its generation is still current, so a racing write wins.
    private ScopeFloor[]? _cachedScopes;
    private int _scopeGeneration;

    internal long CaptureGeneration => Volatile.Read(ref _captureGeneration);

    internal void MarkCapturePublished() => Interlocked.Increment(ref _captureGeneration);

    /// <summary>Orders the caller's preceding reads before the generation load, so a live value read earlier cannot
    /// be judged against a generation sampled before it.</summary>
    internal bool HasCapturedSince(long generation)
    {
        Interlocked.MemoryBarrier();
        return Volatile.Read(ref _captureGeneration) != generation;
    }

    static HistoryAvailability() => Debug.Assert(ScopeRecordPrefix.Length == ScopeRecordPrefixLength);

    public HistoryAvailability(IDb availableBlocks)
    {
        ArgumentNullException.ThrowIfNull(availableBlocks);
        if (availableBlocks is not ISortedKeyValueStore sortedAvailableBlocks)
            throw new ArgumentException($"AvailableBlocks column must be a {nameof(ISortedKeyValueStore)}.", nameof(availableBlocks));

        _availableBlocks = availableBlocks;
        _sortedAvailableBlocks = sortedAvailableBlocks;
    }

    /// <summary>Refuses an index written by an incompatible format. A marker without a format key can only mean a
    /// pre-versioning layout, since every capture batch stamps the version.</summary>
    /// <exception cref="InvalidConfigurationException">The on-disk index uses a different format version.</exception>
    public void VerifyFormat()
    {
        byte[]? version = _availableBlocks.Get(FormatVersionKey);
        if (version is [FormatVersion] or [WindowedFormatVersion]) return;

        bool hasLegacyData = version is not null;
        if (!hasLegacyData)
        {
            // Pre-versioning v1 stamped no format key, so any existing marker means old-layout data.
            foreach (KeyValuePair<byte[], byte[]> _ in _availableBlocks.GetAll())
            {
                hasLegacyData = true;
                break;
            }
        }

        if (hasLegacyData)
        {
            throw new InvalidConfigurationException(
                $"The flat history database was written by an incompatible format " +
                $"(found version {(version is { Length: 1 } ? version[0].ToString() : "none")}, expected {FormatVersion} or {WindowedFormatVersion}). " +
                "Delete the flatHistory database directory to re-capture history, or resync the node.", -1);
        }
    }

    /// <summary>Upgrade-only: once stamped windowed, a DB stays that way regardless of later config.</summary>
    /// <exception cref="InvalidConfigurationException">Windowing configured against existing v2 data - there is no
    /// in-place migration.</exception>
    public byte ResolveFormatVersion(bool windowingConfigured)
    {
        byte? stamped = StampedFormatVersion;
        if (windowingConfigured && stamped == FormatVersion)
        {
            throw new InvalidConfigurationException(
                "HistoryRetention is Rolling, but this flatHistory database already holds history captured in the " +
                "unwindowed (v2) format. Windowing requires the v3 pre-value row format, which cannot be converted " +
                "from existing v2 data in place. Resync the flatHistory database to enable windowing, or set " +
                "HistoryRetention=None and unset HistoryRetentionBlocks to keep running unwindowed against the " +
                "existing data.", -1);
        }

        return windowingConfigured || stamped == WindowedFormatVersion ? WindowedFormatVersion : FormatVersion;
    }

    /// <summary>The highest block H such that every block in <c>[0, H]</c> has been captured; <c>false</c> when none has.</summary>
    public bool TryGetWatermark(out ulong watermark)
    {
        byte[]? value = _availableBlocks.Get(WatermarkKey);
        if (value is not { Length: BlockBytes })
        {
            watermark = 0;
            return false;
        }

        watermark = BinaryPrimitives.ReadUInt64BigEndian(value);
        Volatile.Write(ref _observedWatermark, (long)watermark);
        return true;
    }

    /// <summary>Whether an as-of read at <paramref name="block"/> is backed by contiguous captured history.</summary>
    public bool IsCovered(ulong block)
    {
        long observed = Volatile.Read(ref _observedWatermark);
        if (observed >= 0 && block <= (ulong)observed) return true;
        return TryGetWatermark(out ulong watermark) && block <= watermark;
    }

    /// <summary>Whether <paramref name="block"/> is covered and its captured state root equals <paramref name="stateRoot"/>.</summary>
    public bool Matches(ulong block, in ValueHash256 stateRoot) => !IsBelowGlobalFloor(block) && IsCoveredAndRootMatches(block, stateRoot);

    /// <summary>Covered and root-matching, independent of the global floor, for a per-slice caller.</summary>
    public bool IsCoveredAndRootMatches(ulong block, in ValueHash256 stateRoot)
        => IsCovered(block) && RootMatches(block, stateRoot);

    /// <summary>Whether <paramref name="block"/>'s captured marker equals <paramref name="stateRoot"/> —
    /// independent of coverage.</summary>
    public bool RootMatches(ulong block, in ValueHash256 stateRoot)
    {
        Span<byte> key = stackalloc byte[BlockBytes];
        BinaryPrimitives.WriteUInt64BigEndian(key, block);
        byte[]? capturedRoot = _availableBlocks.Get(key);
        return capturedRoot is { Length: RootBytes } && stateRoot == new ValueHash256(capturedRoot);
    }

    /// <summary>The raw stamped format byte, or <c>null</c> for a fresh DB.</summary>
    internal byte? StampedFormatVersion => _availableBlocks.Get(FormatVersionKey) is { Length: 1 } value ? value[0] : null;

    /// <summary>Reads below it have been pruned. Unset means no floor was ever published.</summary>
    public bool TryGetGlobalFloor(out ulong floor)
    {
        byte[]? value = _availableBlocks.Get(GlobalFloorKey);
        if (value is not { Length: BlockBytes })
        {
            floor = 0;
            return false;
        }

        floor = BinaryPrimitives.ReadUInt64BigEndian(value);
        Volatile.Write(ref _observedFloor, (long)floor);
        return true;
    }

    /// <summary>Whether <paramref name="block"/> sits below the published global floor (has been pruned).</summary>
    public bool IsBelowGlobalFloor(ulong block)
    {
        long observed = Volatile.Read(ref _observedFloor);
        if (observed >= 0 && block < (ulong)observed) return true; // refuse fast path only, see field comment
        return TryGetGlobalFloor(out ulong floor) && block < floor;
    }

    /// <summary>Publish before deleting anything below the floor, so a crash between the two leaves the floor
    /// behind rather than ahead of what is on disk. Unconditional - a pruner uses
    /// <see cref="TryRaiseGlobalFloor"/>.</summary>
    public void PublishGlobalFloor(ulong floor)
    {
        lock (_floorLock)
        {
            WriteGlobalFloorUnderLock(floor);
        }

        Metrics.FlatHistoryFloor = (long)floor;
    }

    /// <summary>Raises the floor only if <paramref name="newFloor"/> is strictly above it.</summary>
    public bool TryRaiseGlobalFloor(ulong newFloor)
    {
        bool raised;
        lock (_floorLock)
        {
            TryGetGlobalFloor(out ulong current);
            raised = newFloor > current;
            if (raised)
            {
                WriteGlobalFloorUnderLock(newFloor);
            }
        }

        return raised;
    }

    private void WriteGlobalFloorUnderLock(ulong floor)
    {
        if (TryGetWatermark(out ulong watermark) && floor > watermark)
        {
            throw new InvalidOperationException(
                $"Refusing to publish flat history floor {floor} above the captured watermark {watermark}: every floor " +
                "is derived from the watermark, so a higher one was computed against inconsistent state.");
        }

        Span<byte> value = stackalloc byte[BlockBytes];
        BinaryPrimitives.WriteUInt64BigEndian(value, floor);
        _availableBlocks.PutSpan(GlobalFloorKey, value);
        _availableBlocks.PutSpan(FormatVersionKey, [WindowedFormatVersion]);
    }

    /// <summary>Refuses on unwindowed v2 data, where a slice is meaningless and stamping would brick the v2 read
    /// path.</summary>
    /// <exception cref="InvalidConfigurationException">The DB is stamped as the unwindowed (v2) format.</exception>
    public void PublishScope(ReadOnlySpan<byte> accountKey, ulong floor)
    {
        lock (_floorLock)
        {
            WriteScopeRecordUnderLock(accountKey, floor);
        }
    }

    public bool TryGetScopeFloor(ReadOnlySpan<byte> accountKey, out ulong floor)
    {
        Span<byte> key = stackalloc byte[ScopeRecordKeyLength];
        EncodeScopeRecordKey(accountKey, key);
        byte[]? value = _availableBlocks.Get(key);
        if (value is not { Length: ScopeRecordValueLength })
        {
            floor = 0;
            return false;
        }

        floor = BinaryPrimitives.ReadUInt64BigEndian(value);
        return true;
    }

    /// <summary>Per-slice counterpart to <see cref="TryRaiseGlobalFloor"/>. False when the scope does not
    /// exist - never creates one.</summary>
    public bool TryRaiseScopeFloor(ReadOnlySpan<byte> accountKey, ulong newFloor)
    {
        bool raised;
        lock (_floorLock)
        {
            raised = TryGetScopeFloor(accountKey, out ulong current) && newFloor > current;
            if (raised)
            {
                WriteScopeRecordUnderLock(accountKey, newFloor);
            }
        }

        return raised;
    }

    /// <summary>A removed address reverts to the all-keys scope, so its rows become prunable again.</summary>
    public void RemoveScope(ReadOnlySpan<byte> accountKey)
    {
        lock (_floorLock)
        {
            Span<byte> key = stackalloc byte[ScopeRecordKeyLength];
            EncodeScopeRecordKey(accountKey, key);
            _availableBlocks.Remove(key);
            InvalidateScopeCache();
        }
    }

    private void WriteScopeRecordUnderLock(ReadOnlySpan<byte> accountKey, ulong floor)
    {
        byte? stamped = StampedFormatVersion;
        if (stamped == FormatVersion)
        {
            throw new InvalidConfigurationException(
                "Cannot publish a flat history slice scope: this database is stamped as the unwindowed (v2) format, " +
                "which retains everything already and cannot represent a narrower or wider per-address floor.", -1);
        }

        Span<byte> key = stackalloc byte[ScopeRecordKeyLength];
        EncodeScopeRecordKey(accountKey, key);

        Span<byte> value = stackalloc byte[ScopeRecordValueLength];
        BinaryPrimitives.WriteUInt64BigEndian(value, floor);

        _availableBlocks.PutSpan(key, value);
        if (stamped != WindowedFormatVersion)
        {
            _availableBlocks.PutSpan(FormatVersionKey, [WindowedFormatVersion]);
        }

        InvalidateScopeCache();
    }

    private static void EncodeScopeRecordKey(ReadOnlySpan<byte> accountKey, Span<byte> buffer)
    {
        RequireScopeKey(accountKey);
        ScopeRecordPrefix.CopyTo(buffer);
        accountKey.CopyTo(buffer[ScopeRecordPrefixLength..]);
    }

    /// <summary>Truncating a wider key would let a publish succeed while lookups miss it.</summary>
    private static void RequireScopeKey(ReadOnlySpan<byte> key)
    {
        if (key.Length != HistoryKeyLayout.ScopeKeyLength)
        {
            throw new ArgumentException(
                $"A retention scope is keyed by exactly {HistoryKeyLayout.ScopeKeyLength} bytes; got {key.Length}.", nameof(key));
        }
    }

    private void InvalidateScopeCache()
    {
        Interlocked.Increment(ref _scopeGeneration);
        Volatile.Write(ref _cachedScopes, null);
    }

    /// <summary>Every configured point scope, cached until the next <see cref="PublishScope"/>/<see cref="RemoveScope"/>.
    /// Wrapped so a caller cannot mutate the shared cache through the returned reference; <see cref="GetScopesArray"/>
    /// is the zero-wrap internal counterpart for the pruner's hot per-group path.</summary>
    public IReadOnlyList<ScopeFloor> GetScopes() => new ReadOnlyCollection<ScopeFloor>(GetScopesArray());

    internal ScopeFloor[] GetScopesArray()
    {
        ScopeFloor[]? cached = Volatile.Read(ref _cachedScopes);
        if (cached is not null) return cached;

        int generationBeforeScan = Volatile.Read(ref _scopeGeneration);
        ScopeFloor[] scanned = ScanScopeRecords();

        // Publish only if no writer bumped the generation while this scan was running - otherwise the scan may
        // have raced a concurrent Publish/Remove and reflects a torn view. Leaving the cache null makes the next
        // caller rescan instead of trusting a stale result; this call still returns its own (possibly torn) scan
        // rather than retrying, since a fresh caller immediately after will simply rescan for real.
        if (Volatile.Read(ref _scopeGeneration) == generationBeforeScan)
        {
            Volatile.Write(ref _cachedScopes, scanned);
        }

        return scanned;
    }

    private ScopeFloor[] ScanScopeRecords()
    {
        List<ScopeFloor> scopes = [];
        byte[] upperBound = new byte[ScopeRecordPrefixLength + HistoryKeyLayout.ScopeKeyLength + 1];
        ScopeRecordPrefix.CopyTo(upperBound);
        upperBound.AsSpan(ScopeRecordPrefixLength).Fill(0xFF);

        using ISortedView view = _sortedAvailableBlocks.GetViewBetween(ScopeRecordPrefix, upperBound);
        while (view.MoveNext())
        {
            if (view.CurrentKey.Length != ScopeRecordKeyLength) continue;
            if (view.CurrentValue.Length != ScopeRecordValueLength) continue;

            byte[] key = view.CurrentKey[ScopeRecordPrefixLength..].ToArray();
            ulong floor = BinaryPrimitives.ReadUInt64BigEndian(view.CurrentValue);
            scopes.Add(new ScopeFloor(key, floor, IsGeneral: false));
        }

        return scopes.ToArray();
    }

    /// <summary>Resolves the narrowest applicable floor for <paramref name="key"/> - its own point scope if one is
    /// configured, else the all-keys scope at <paramref name="knownGeneralFloor"/>. Takes the caller's own already-known
    /// general floor so a per-group hot-path caller (the pruner) never re-reads the DB for it.</summary>
    public ScopeFloor ResolveScope(ReadOnlySpan<byte> key, ulong knownGeneralFloor)
    {
        RequireScopeKey(key);
        ScopeFloor[] scopes = GetScopesArray();
        for (int i = 0; i < scopes.Length; i++)
        {
            if (scopes[i].Key.AsSpan().SequenceEqual(key)) return scopes[i];
        }

        return new ScopeFloor([], knownGeneralFloor, IsGeneral: true);
    }

    public ScopeFloor ResolveScope(ReadOnlySpan<byte> key) => ResolveScope(key, GeneralFloorOrZero());

    private ulong GeneralFloorOrZero()
    {
        TryGetGlobalFloor(out ulong floor);
        return floor;
    }

    /// <summary>Records the per-block marker (<c>block -> captured state root</c>) into <paramref name="batch"/>.</summary>
    /// <remarks>Stamps <paramref name="formatVersion"/> atomically with the marker (a marker without a format key
    /// must never be observable on disk); pass <paramref name="stampFormat"/> false only once a publish already
    /// made the stamp durable.</remarks>
    public static void MarkBlock(IWriteBatch batch, ulong block, in ValueHash256 stateRoot, byte formatVersion, bool stampFormat = true)
    {
        Span<byte> key = stackalloc byte[BlockBytes];
        BinaryPrimitives.WriteUInt64BigEndian(key, block);
        batch.PutSpan(key, stateRoot.Bytes);
        if (stampFormat) batch.PutSpan(FormatVersionKey, [formatVersion]);
    }

    /// <summary>
    /// Publishes the contiguous watermark (and stamps <paramref name="formatVersion"/>). Written outside the
    /// per-block capture batches so it advances only after the whole captured range is durable — a partial or
    /// failed capture leaves the watermark where it was, so reads above the gap fail closed.
    /// </summary>
    public void PublishWatermark(ulong watermark, byte formatVersion)
    {
        if (TryGetWatermark(out ulong current) && current >= watermark)
        {
            return;
        }

        Span<byte> value = stackalloc byte[BlockBytes];
        BinaryPrimitives.WriteUInt64BigEndian(value, watermark);
        _availableBlocks.PutSpan(WatermarkKey, value);
        _availableBlocks.PutSpan(FormatVersionKey, [formatVersion]);
    }
}
