// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Diagnostics;
using System.Collections.ObjectModel;
using Nethermind.Core;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.State.Flat.Persistence;

namespace Nethermind.State.Flat.History;

/// <summary>
/// The <c>AvailableBlocks</c> column: per-block markers (<c>[block BE] -> 32-byte captured state root</c>) plus two
/// reserved keys — a contiguous-from-genesis watermark and a format version. An as-of read at block H is served only
/// when <c>H &lt;= watermark</c> (every block in <c>[0, H]</c> was captured, so a floor-seek cannot silently shadow a
/// gap) <em>and</em> the queried state root matches the captured root (so a non-canonical EIP-1898 hash below the
/// barrier is rejected rather than served the canonical value).
/// </summary>
public sealed class HistoryAvailability
{
    // No ChangeSets columns, descending block suffix, and a whole-trie-path account key — data stamped 3 or below
    // keys accounts by a truncated path and is unreadable under these seeks.
    internal const byte FormatVersion = 4;

    // Stamped the first time a retention floor is ever published on this DB. Older, floor-unaware binaries must
    // refuse a windowed DB outright (VerifyFormat rejects it) rather than silently read pruned rows as absent.
    internal const byte WindowedFormatVersion = 5;

    private const int BlockBytes = sizeof(ulong);
    private const int RootBytes = 32;

    // Reserved keys, deliberately not BlockBytes long so they can never collide with a per-block marker key.
    private static ReadOnlySpan<byte> WatermarkKey => "history:watermark"u8;
    private static ReadOnlySpan<byte> FormatVersionKey => "history:format"u8;

    private static ReadOnlySpan<byte> GlobalFloorKey => "history:floor:global"u8;

    // Point scopes only: one record per configured address, keyed by its own account-key bytes. A future range
    // scope (multiple addresses sharing one record) is a new record kind under a different prefix, not a retrofit
    // of this one - see SliceScopeConfig's remarks.
    private static ReadOnlySpan<byte> ScopeRecordPrefix => "history:floor:scope:"u8;
    private const int ScopeRecordPrefixLength = 20; // == ScopeRecordPrefix.Length, asserted in the static ctor
    private const int ScopeRecordKeyLength = ScopeRecordPrefixLength + HistoryKeyLayout.ScopeKeyLength;
    private const int ScopeRecordValueLength = BlockBytes;

    private readonly IDb _availableBlocks;
    private readonly ISortedKeyValueStore _sortedAvailableBlocks;
    private readonly Lock _floorLock = new();

    // Monotonic fast path: never used to refuse (a miss re-reads), so a reader instance cannot lag the writer.
    private long _observedWatermark = -1;

    // The mirror-image fast path: refusing early is safe because the floor only ever moves up (a cached value
    // then never overstates how much is retained). An accept always re-reads the DB regardless, so a read can
    // never slip in below a floor that advanced since the cache was last observed.
    private long _observedFloor = -1;

    // Generation-guarded cache: a scan started before a concurrent Publish/Remove could otherwise publish a torn
    // view. Writers bump the generation under _floorLock; a scan only publishes if the generation it started with
    // is still current when it finishes, so a racing write always wins and the next reader rescans instead of
    // trusting a result that may have missed that write.
    private ScopeFloor[]? _cachedScopes;
    private int _scopeGeneration;

    static HistoryAvailability() => Debug.Assert(ScopeRecordPrefix.Length == ScopeRecordPrefixLength);

    public HistoryAvailability(IDb availableBlocks)
    {
        ArgumentNullException.ThrowIfNull(availableBlocks);
        if (availableBlocks is not ISortedKeyValueStore sortedAvailableBlocks)
            throw new ArgumentException($"AvailableBlocks column must be a {nameof(ISortedKeyValueStore)}.", nameof(availableBlocks));

        _availableBlocks = availableBlocks;
        _sortedAvailableBlocks = sortedAvailableBlocks;
    }

    /// <summary>
    /// Refuses a history index written by an incompatible format. A fresh/empty index passes; every capture batch
    /// stamps the version atomically via <see cref="MarkBlock"/>, so a marker without a format key can only mean a
    /// pre-versioning layout.
    /// </summary>
    /// <exception cref="InvalidConfigurationException">The on-disk index uses a different format version.</exception>
    public void VerifyFormat()
    {
        byte[]? version = _availableBlocks.Get(FormatVersionKey);
        if (version is [FormatVersion] or [WindowedFormatVersion]) return;

        bool hasLegacyData = version is not null;
        if (!hasLegacyData)
        {
            // Pre-versioning v1 stamped no format key, so any existing marker means old-layout data.
            foreach (KeyValuePair<byte[], byte[]?> _ in _availableBlocks.GetAll())
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

    /// <summary>Resolves the row format upgrade-only: once stamped <see cref="WindowedFormatVersion"/>, a DB stays
    /// that way regardless of later config - v2 and v3 rows are incompatible shapes in the same column.</summary>
    /// <exception cref="InvalidConfigurationException">Windowing configured against existing v2 data - there is no
    /// in-place migration.</exception>
    public byte ResolveFormatVersion(bool windowingConfigured)
    {
        byte? stamped = StampedFormatVersion;
        if (windowingConfigured && stamped == FormatVersion)
        {
            throw new InvalidConfigurationException(
                "HistoryRetentionBlocks is set, but this flatHistory database already holds history captured in the " +
                "unwindowed (v2) format. Windowing requires the v3 pre-value row format, which cannot be converted " +
                "from existing v2 data in place. Resync the flatHistory database to enable windowing, or unset " +
                "HistoryRetentionBlocks to keep running unwindowed against the existing data.", -1);
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

    /// <summary>Whether <paramref name="block"/> is covered and its captured state root equals <paramref name="stateRoot"/> —
    /// deliberately independent of the global floor, so a restricted (per-slice) caller can re-verify canonicity for
    /// a block it already knows sits below that floor.</summary>
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

    /// <summary>The retention floor for the all-keys (global) scope: reads below it have been pruned. Unset — the
    /// default, unwindowed case — means no floor has ever been published, so nothing is refused on that basis.</summary>
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

    /// <summary>
    /// Publishes the retention floor for the all-keys scope and stamps the windowed format version. Callers must
    /// publish before deleting anything below it: a crash between the two leaves the floor honestly behind (never
    /// ahead of) what is still on disk, mirroring <see cref="PublishWatermark"/>'s fail-closed ordering.
    /// Unconditional — for the initial seed (there is no prior floor to race against). A pruner raising the floor
    /// must go through <see cref="TryRaiseGlobalFloor"/> instead.
    /// </summary>
    public void PublishGlobalFloor(ulong floor)
    {
        lock (_floorLock)
        {
            WriteGlobalFloorUnderLock(floor);
        }

    }

    /// <summary>
    /// Raises the retention floor if and only if <paramref name="newFloor"/> is strictly above the current value.
    /// Returns whether the floor actually moved.
    /// </summary>
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

        if (raised)
        {
        }

        return raised;
    }

    private void WriteGlobalFloorUnderLock(ulong floor)
    {
        Span<byte> value = stackalloc byte[BlockBytes];
        BinaryPrimitives.WriteUInt64BigEndian(value, floor);
        _availableBlocks.PutSpan(GlobalFloorKey, value);
        _availableBlocks.PutSpan(FormatVersionKey, [WindowedFormatVersion]);
    }

    /// <summary>Creates or overwrites the point scope for <paramref name="accountKey"/>. Refuses (never silently
    /// stamps) when the resolved on-disk format is the unwindowed v2 - a slice is meaningless there (everything is
    /// already retained) and stamping the windowed format onto live v2 data would brick it for the v2 read path.</summary>
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

    /// <summary>Raises one scope's own floor if and only if <paramref name="newFloor"/> is strictly above its
    /// current value - the pruner's per-slice counterpart to <see cref="TryRaiseGlobalFloor"/>, for a slice
    /// configured with a bounded (not unbounded) retention. Returns <c>false</c> (never creates) when the scope
    /// does not already exist.</summary>
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

        if (raised)
        {
        }

        return raised;
    }

    /// <summary>Deletes a scope record - an address removed from the operator's allow-list reverts to the all-keys
    /// scope, so its rows below the general floor become prunable again on the pruner's next pass.</summary>
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

    /// <summary>Scopes are keyed by <see cref="HistoryKeyLayout.ScopeKeyLength"/> bytes (a storage row carries only
    /// that much of its address); silently truncating a wider key would let a publish "succeed" while lookups miss
    /// it and the slice's rows get pruned.</summary>
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
