// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.PersistedSnapshots;
using Nethermind.State.Flat.PersistedSnapshots.Storage;

namespace Nethermind.State.Flat.History;

/// <summary>
/// Captures finalized per-block changesets into the history columns before the per-block snapshots are pruned,
/// using the exact flat key/value encoders so the recorded bytes match the live flat columns. Deleted accounts
/// and zeroed/removed slots are recorded as empty tombstones.
/// </summary>
public sealed class HistoryWriter : IFlatPersistenceCaptureHook, IStateHistoryCaptureStatus, IHistoryPivotSeeder
{
    // A capture failing forever would stall persistence and grow the in-memory tier until OOM; degrade instead.
    private const int MaxConsecutiveCaptureFailures = 16;

    // Hard cap on per-slot destruct enumeration (v3 only): above this, materializing pre-value rows for every
    // wiped slot is unbounded work on a single block. A poisoned marker is cheap and fails closed instead.
    // Internal so tests can exercise the exact boundary without duplicating the number.
    internal const int DestructSlotEnumerationCap = 10_000;

    private readonly IColumnsDb<FlatHistoryColumns> _history;
    private readonly HistoryStore? _accountHistory;
    private readonly HistoryStore? _storageHistory;
    private readonly HistoryStoreV3? _accountHistoryV3;
    private readonly HistoryStoreV3? _storageHistoryV3;
    // The persisted (never tip/snapshot-stacked) live flat columns. Safe to read during capture because capture
    // always runs strictly before this round's flat persist commits — see the format-version field comment and
    // HistoryStoreV3's remarks for the invariant chain this relies on.
    private readonly IDb _persistedAccounts;
    private readonly IDb _persistedStorage;
    // v3 only: enumerates a destructed account's persisted (pre-destruct) slots so they can be materialized as
    // per-slot pre-value rows. Default/unused when !_isV3.
    private readonly BaseFlatPersistence.Reader _persistedFlatReader;
    private readonly StorageClearStore _storageClears;
    private readonly ChangesetSidecarStore? _changesetSidecar;
    private readonly HistoryAvailability _availability;
    private readonly bool _rlpWrapSlots;
    private readonly bool _isV3;
    private readonly bool _enabled;
    private readonly ILogger _logger;

    // Under the persistence lock a failed lease means the range below is gone for good (history enabled mid-life);
    // further captures would only write rows above a gap no read can cross, so skip them until restart.
    private volatile bool _permanentGapDetected;
    private int _consecutiveCaptureFailures;
    // Config cannot prove the hook is wired (a patricia backend constructs this writer but never invokes it).
    private volatile bool _captureProven;

    // Resolved once at construction, then fixed for the process lifetime: upgrade-only, never derived from config
    // alone on every write. A windowed-configured node declares itself v3 from its very first capture, before any
    // floor exists (so the format key protects it immediately). But config can legally change between restarts —
    // if this instead recomputed "windowed = config wants it" fresh on every call, a node started once with a
    // window (stamping v3, publishing a floor, pruning rows) and then restarted with HistoryRetentionBlocks
    // reverted to 0 would have its very next captured block silently restamp the DB back to v2, even though the
    // floor key and the already-pruned gaps are still on disk. Latching from persisted state as well as config
    // closes that: once a DB has ever been windowed, it stays declared windowed regardless of later config.
    private readonly byte _formatVersion;

    public HistoryWriter(IColumnsDb<FlatDbColumns> db, IColumnsDb<FlatHistoryColumns> history, IFlatDbConfig config, ILogManager logManager)
    {
        ArgumentNullException.ThrowIfNull(history);
        ILogger logger = logManager.GetClassLogger<HistoryWriter>();
        _enabled = config.HistoryEnabled;
        _history = history;
        _rlpWrapSlots = BasePersistence.ResolveSlotEncoding(
            db,
            (ISortedKeyValueStore)db.GetColumnDb(FlatDbColumns.Storage),
            logger);
        _logger = logger;
        _persistedAccounts = db.GetColumnDb(FlatDbColumns.Account);
        _persistedStorage = db.GetColumnDb(FlatDbColumns.Storage);
        _storageClears = new StorageClearStore(history.GetColumnDb(FlatHistoryColumns.StorageClears));
        _changesetSidecar = config.HistoryChangesetSidecarEnabled
            ? new ChangesetSidecarStore(history.GetColumnDb(FlatHistoryColumns.ChangesetSidecar))
            : null;
        _availability = new HistoryAvailability(history.GetColumnDb(FlatHistoryColumns.AvailableBlocks));
        _formatVersion = _availability.ResolveFormatVersion(config.HistoryRetentionBlocks > 0);
        _isV3 = _formatVersion == HistoryAvailability.WindowedFormatVersion;
        if (_isV3)
        {
            _accountHistoryV3 = new HistoryStoreV3(history.GetColumnDb(FlatHistoryColumns.AccountHistory));
            _storageHistoryV3 = new HistoryStoreV3(history.GetColumnDb(FlatHistoryColumns.StorageHistory));
            _persistedFlatReader = new BaseFlatPersistence.Reader(
                (ISortedKeyValueStore)_persistedAccounts,
                (ISortedKeyValueStore)_persistedStorage,
                rlpWrapSlots: _rlpWrapSlots);
        }
        else
        {
            _accountHistory = new HistoryStore(history.GetColumnDb(FlatHistoryColumns.AccountHistory), logger);
            _storageHistory = new HistoryStore(history.GetColumnDb(FlatHistoryColumns.StorageHistory), logger);
        }

        if (_enabled)
        {
            _availability.VerifyFormat();
            Metrics.FlatHistoryWatermark = (long)LastCapturedBlock;
        }
    }

    /// <inheritdoc/>
    public bool CaptureHealthy => _enabled && !_permanentGapDetected && _captureProven;

    /// <inheritdoc/>
    public event Action<ulong>? WatermarkAdvanced;

    /// <inheritdoc/>
    public event Action? CaptureDisabled;

    /// <summary>The contiguous-from-genesis watermark: the highest block a read is served for; 0 when none captured.</summary>
    public ulong LastCapturedBlock => _availability.TryGetWatermark(out ulong watermark) ? watermark : 0;

    /// <summary>
    /// Captures the changeset of every not-yet-captured block on <paramref name="persistedHead"/>'s chain, up to and
    /// including it, advances the contiguous watermark, and makes both crash-durable before returning.
    /// </summary>
    /// <remarks>
    /// Walks backwards through each base's <see cref="Snapshot.From"/> link (one base == one block's changeset),
    /// leasing from the persisted tier when long-finality Phase 2 converted the in-memory copy away, until it
    /// connects to the existing watermark (or genesis). The watermark gates reads and advances only on a connect,
    /// so a partial capture fails closed. On a connect the history WAL is synced before returning — the flat
    /// persist commits only after, and must never get ahead of durable history.
    /// </remarks>
    public void CaptureUpTo(in StateId persistedHead, ISnapshotRepository snapshotRepository, CancellationToken cancellationToken)
    {
        if (!_enabled || _permanentGapDetected) return;

        try
        {
            CaptureUpToCore(persistedHead, snapshotRepository, cancellationToken);
            _consecutiveCaptureFailures = 0;
            _captureProven = true;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            if (++_consecutiveCaptureFailures >= MaxConsecutiveCaptureFailures)
            {
                DisableCapture(
                    $"History capture disabled after {MaxConsecutiveCaptureFailures} consecutive failures (last: {e.Message}). " +
                    $"Flat persistence resumes without capture; as-of reads above block {LastCapturedBlock} report no history. " +
                    "Resync the flatHistory database to re-enable.");
            }
            throw;
        }
    }

    private void CaptureUpToCore(in StateId persistedHead, ISnapshotRepository snapshotRepository, CancellationToken cancellationToken)
    {
        ulong target = persistedHead.BlockNumber;
        bool hasWatermark = _availability.TryGetWatermark(out ulong watermark);
        if (hasWatermark && target <= watermark) return;

        // v3 only: per-key "oldest touch in this walk still waiting to learn its pre-value" — resolved either by
        // an even older touch of the same key later in the same walk (see RecordAccountV3/RecordStorageV3), or,
        // for whatever remains once the walk connects, by ResolvePendingV3 below. Not needed for v2, which
        // records a self-contained post-value per touch and has no such deferred state.
        PendingV3Writes? pending = _isV3 ? new PendingV3Writes() : null;

        StateId current = persistedHead;
        bool connected = false;
        while (current != StateId.PreGenesis)
        {
            // A first capture can span the whole in-memory depth under the persistence lock; stay responsive to
            // shutdown. Throwing (never returning) aborts the caller's persist, so the sources survive for a retry.
            cancellationToken.ThrowIfCancellationRequested();

            if (hasWatermark && current.BlockNumber <= watermark)
            {
                // A number-only connect would strand a reorged pre-finalization capture under a healthy watermark.
                if (!_availability.Matches(current.BlockNumber, current.StateRoot))
                {
                    DisableCapture(
                        $"History capture stopped: the captured state root at block {current.BlockNumber} does not match " +
                        $"the chain being persisted ({current.StateRoot}) - a pre-finalization capture was reorged away. " +
                        $"The watermark stays at {watermark}; resync the flatHistory database to re-enable capture.");
                    return;
                }

                connected = true;
                break;
            }

            if (snapshotRepository.TryLeaseInMemoryState(current, SnapshotTier.InMemoryBase, out Snapshot? snapshot))
            {
                using (snapshot)
                {
                    CaptureBlock(current.BlockNumber, current.StateRoot, snapshot, pending);
                    current = snapshot.From;
                }
            }
            else if (snapshotRepository.TryLeaseBasePersistedSnapshot(current, out PersistedSnapshot? persisted))
            {
                using (persisted)
                {
                    CaptureBlock(current.BlockNumber, current.StateRoot, persisted, pending);
                    current = persisted.From;
                }
            }
            else
            {
                break;
            }
        }

        if (current == StateId.PreGenesis) connected = true;

        if (connected)
        {
            if (pending is not null) ResolvePendingV3(pending);

            // Durable (throwing WAL-sync) before the caller persists the flat state and prunes the sources.
            _availability.PublishWatermark(target, _formatVersion);
            _history.SyncWal();
            Metrics.FlatHistoryWatermark = (long)target;

            try
            {
                WatermarkAdvanced?.Invoke(target);
            }
            catch (Exception e)
            {
                // Contained: the watermark has already published, so letting this out would abort the persist.
                if (_logger.IsError) _logger.Error($"A watermark-advanced handler failed at block {target}.", e);
            }
        }
        else
        {
            // With capture ordered before every persist/prune, an unconnectable walk only happens when history was
            // enabled mid-life — permanent, so stop capturing instead of stalling or rewriting dead rows.
            DisableCapture($"History capture stopped at {current} without connecting to the captured range - " +
                $"the blocks below were pruned before history was enabled. The watermark stays at " +
                $"{(hasWatermark ? watermark.ToString() : "none")}; as-of reads above it report no history, and capture is disabled until restart.");
        }
    }

    /// <summary>Permanently stops capture for this process, notifying dependants so they can persist retained data
    /// before the pending persist prunes the blocks above the watermark.</summary>
    private void DisableCapture(string reason)
    {
        _permanentGapDetected = true;
        Metrics.FlatHistoryCaptureDisabled = 1;
        if (_logger.IsError) _logger.Error(reason +
            " If receipt derivation is enabled, the receipt bodies retained for blocks above the watermark are persisted now (see the receipt store's log for the outcome).");

        try
        {
            CaptureDisabled?.Invoke();
        }
        catch (Exception e)
        {
            // Contained: disabling is one-shot, so a retry would prune without ever re-notifying the handler.
            if (_logger.IsError) _logger.Error("A capture-disabled handler failed; data retained for blocks above the watermark may be lost.", e);
        }
    }

    /// <summary>
    /// Seeds the block-0 changeset from the chain's initial allocations, for a node that cannot capture genesis via
    /// the walk — without it a dormant genesis allocation reads as absent at every height.
    /// </summary>
    /// <remarks>
    /// Under v3 this needs no rows at all: a query at block 0 finding no captured change forward-seeks to nothing
    /// (nothing has been captured yet) and correctly falls through to the persisted flat column, which already
    /// holds the genesis allocations once genesis itself is processed — see <see cref="HistoryStoreV3"/>'s remarks
    /// for why that fallback is sound. Under v2 there is no such fallback, so the allocations must be written as
    /// explicit rows. Must run at startup before block processing: it writes without the persistence lock that
    /// serializes <see cref="CaptureUpTo"/>, so it must not overlap a capture.
    /// </remarks>
    [SkipLocalsInit]
    public void SeedGenesis(IReadOnlyCollection<KeyValuePair<Address, Account>> allocations, in ValueHash256 genesisStateRoot)
    {
        if (!_enabled) return;

        using (IColumnsWriteBatch<FlatHistoryColumns> batch = _history.StartWriteBatch())
        {
            HistoryColumnBatches columns = new(batch);
            HistoryAvailability.MarkBlock(columns.AvailableBlocks, 0, genesisStateRoot, _formatVersion);

            if (!_isV3)
            {
                Span<byte> accountKey = stackalloc byte[BaseFlatPersistence.AccountKeyLength];
                foreach (KeyValuePair<Address, Account> allocation in allocations)
                {
                    RecordAccount(0, allocation.Key.ToAccountPath, allocation.Value, accountKey, in columns);
                }
            }
        }

        // The genesis floor a later walk connects to. Deliberately not proof of health: the nodes that reach the
        // seed (history enabled mid-life) are exactly the ones whose first walk fails.
        _availability.PublishWatermark(0, _formatVersion);
        _history.SyncWal();
    }

    /// <summary>
    /// Seeds a windowed node's floor at a snap-sync pivot instead of genesis: publishes watermark = floor = pivot
    /// so a later capture walk connects to it through the existing watermark-based connect path unchanged — a
    /// pivot seed is just a watermark reset at a non-zero block, the same mechanism <see cref="SeedGenesis"/>
    /// already uses at block 0.
    /// </summary>
    /// <remarks>
    /// No account/storage rows are written, and none are needed: pivot-start is a v3-only mode (a query at or
    /// after the pivot that finds no captured change forward-seeks to nothing and correctly falls through to the
    /// persisted flat column, which already holds exactly the pivot's state — sync has just finished writing it,
    /// nothing else has run yet). A full state copy was only ever needed to support v2 semantics, which has no
    /// such fallback; this is a no-op on a v2 (unwindowed) writer rather than publishing a floor v2 cannot back —
    /// pivot-starting is simply not supported for v2 in this pass, the same as today (before this feature
    /// existed) for any node syncing without flat history. Call at the sync-completion seam (mirroring how
    /// <see cref="IStateBoundaryWriter.OldestStateBlock"/> is advanced there for the trie backend) before any
    /// block has been processed on top of the pivot.
    /// </remarks>
    public void SeedPivot(ulong pivotBlock, in ValueHash256 pivotStateRoot, IPersistence.IPersistenceReader reader)
    {
        if (!_enabled || !_isV3) return;
        _ = reader; // no state scan needed under v3 — kept in the signature for the IHistoryPivotSeeder contract

        using (IColumnsWriteBatch<FlatHistoryColumns> batch = _history.StartWriteBatch())
        {
            HistoryColumnBatches columns = new(batch);
            HistoryAvailability.MarkBlock(columns.AvailableBlocks, pivotBlock, pivotStateRoot, _formatVersion);
        }

        _availability.PublishWatermark(pivotBlock, _formatVersion);
        _availability.PublishGlobalFloor(pivotBlock);
        _history.SyncWal();
    }

    [SkipLocalsInit]
    private void CaptureBlock(ulong block, in ValueHash256 stateRoot, Snapshot snapshot, PendingV3Writes? pending)
    {
        using IColumnsWriteBatch<FlatHistoryColumns> batch = _history.StartWriteBatch();
        HistoryColumnBatches columns = new(batch);
        HistoryAvailability.MarkBlock(columns.AvailableBlocks, block, stateRoot, _formatVersion);

        Span<byte> accountKey = stackalloc byte[BaseFlatPersistence.AccountKeyLength];
        foreach (KeyValuePair<HashedKey<Address>, bool> destructed in snapshot.SelfDestructedStorageAddresses)
        {
            // Value == true means the account had no persisted storage before the destruct; PersistenceManager
            // skips the flat range-delete in that case, so there is nothing in history to shadow either.
            if (destructed.Value) continue;

            ValueHash256 addrHash = destructed.Key.Key.ToAccountPath;
            ReadOnlySpan<byte> destructedAccountKey = BaseFlatPersistence.EncodeAccountKeyHashed(accountKey, addrHash);
            _storageClears.RecordClear(block, destructedAccountKey, columns.StorageClears);

            if (_isV3)
            {
                HandleSelfDestructV3(block, addrHash, destructedAccountKey, pending!, columns.StorageHistory, columns.StorageClears);
            }
        }

        foreach (KeyValuePair<HashedKey<Address>, Account?> change in snapshot.Accounts)
        {
            if (_isV3)
                RecordAccountV3(block, change.Key.Key.ToAccountPath, change.Value, accountKey, pending!, columns.AccountHistory);
            else
                RecordAccount(block, change.Key.Key.ToAccountPath, change.Value, accountKey, in columns);
        }

        Span<byte> storageKey = stackalloc byte[BaseFlatPersistence.StorageKeyLength];
        Span<byte> storageValue = stackalloc byte[BaseFlatPersistence.RlpSlotValueBufferSize];
        foreach (KeyValuePair<HashedKey<(Address, UInt256)>, SlotValue?> change in snapshot.Storages)
        {
            (Address addr, UInt256 slot) = change.Key.Key;
            if (_isV3)
                RecordStorageV3(block, addr.ToAccountPath, slot, change.Value, storageKey, storageValue, pending!, columns.StorageHistory);
            else
                RecordStorage(block, addr.ToAccountPath, slot, change.Value, storageKey, storageValue, in columns);
        }

        if (_changesetSidecar is not null)
        {
            RecordChangesetSidecarChunk(block, snapshot, batch);
        }
    }

    /// <summary>
    /// Encodes the block's changeset, grouped by address (BAL-shaped), and writes it into the sidecar via
    /// <see cref="ChangesetSidecarStore.RecordChangeset"/>, which splits it across contiguous chunks if it exceeds
    /// the per-chunk cap — a destruct-heavy block (see <see cref="HandleSelfDestructV3"/>'s up-to-
    /// <see cref="DestructSlotEnumerationCap"/> slots) is exactly the shape that can.
    /// </summary>
    private void RecordChangesetSidecarChunk(ulong block, Snapshot snapshot, IColumnsWriteBatch<FlatHistoryColumns> batch)
    {
        Dictionary<Address, List<ChangesetSlotEntry>> storageByAddress = [];
        foreach (KeyValuePair<HashedKey<(Address, UInt256)>, SlotValue?> change in snapshot.Storages)
        {
            (Address address, UInt256 slot) = change.Key.Key;
            if (!storageByAddress.TryGetValue(address, out List<ChangesetSlotEntry>? slots))
            {
                slots = [];
                storageByAddress[address] = slots;
            }

            byte[] value = change.Value is SlotValue slotValue ? slotValue.AsReadOnlySpan.WithoutLeadingZeros().ToArray() : [];
            slots.Add(new ChangesetSlotEntry(slot, value));
        }

        List<ChangesetAccountEntry> entries = new(snapshot.AccountsCount + storageByAddress.Count);
        foreach (KeyValuePair<HashedKey<Address>, Account?> change in snapshot.Accounts)
        {
            Address address = change.Key.Key;
            byte[] accountValue;
            if (change.Value is Account account)
            {
                using ArrayPoolSpan<byte> rlp = AccountDecoder.Slim.EncodeToArrayPoolSpan(account);
                accountValue = ((ReadOnlySpan<byte>)rlp).ToArray();
            }
            else
            {
                accountValue = [];
            }

            storageByAddress.Remove(address, out List<ChangesetSlotEntry>? storageChanges);
            entries.Add(new ChangesetAccountEntry(address, AccountChanged: true, accountValue, storageChanges ?? []));
        }

        foreach (KeyValuePair<Address, List<ChangesetSlotEntry>> remaining in storageByAddress)
        {
            entries.Add(new ChangesetAccountEntry(remaining.Key, AccountChanged: false, ReadOnlyMemory<byte>.Empty, remaining.Value));
        }

        byte[] payload = ChangesetChunkCodec.Encode(entries);
        _changesetSidecar!.RecordChangeset(block, payload, batch.GetColumnBatch(FlatHistoryColumns.ChangesetSidecar));
    }

    /// <summary>
    /// Captures a block whose in-memory base was converted to the persisted tier by long-finality Phase 2 — the
    /// persisted base holds the same one-block changeset.
    /// </summary>
    [SkipLocalsInit]
    private void CaptureBlock(ulong block, in ValueHash256 stateRoot, PersistedSnapshot snapshot, PendingV3Writes? pending)
    {
        using WholeReadSession session = snapshot.BeginWholeReadSession();
        WholeReadScanner scanner = PersistedSnapshotScanner.ForWholeRead(session, snapshot);

        using IColumnsWriteBatch<FlatHistoryColumns> batch = _history.StartWriteBatch();
        HistoryColumnBatches columns = new(batch);
        HistoryAvailability.MarkBlock(columns.AvailableBlocks, block, stateRoot, _formatVersion);

        Span<byte> accountKey = stackalloc byte[BaseFlatPersistence.AccountKeyLength];
        Span<byte> storageKey = stackalloc byte[BaseFlatPersistence.StorageKeyLength];
        Span<byte> storageValue = stackalloc byte[BaseFlatPersistence.RlpSlotValueBufferSize];
        foreach (WholeReadScanner.PerAddressEntry entry in scanner.PerAddresses)
        {
            ValueHash256 addrHash = entry.Address.ToAccountPath;

            if (entry.SelfDestructFlag is false)
            {
                ReadOnlySpan<byte> destructedAccountKey = BaseFlatPersistence.EncodeAccountKeyHashed(accountKey, addrHash);
                _storageClears.RecordClear(block, destructedAccountKey, columns.StorageClears);

                if (_isV3)
                {
                    HandleSelfDestructV3(block, addrHash, destructedAccountKey, pending!, columns.StorageHistory, columns.StorageClears);
                }
            }

            if (entry.HasAccount)
            {
                if (_isV3)
                    RecordAccountV3(block, addrHash, entry.Account, accountKey, pending!, columns.AccountHistory);
                else
                    RecordAccount(block, addrHash, entry.Account, accountKey, in columns);
            }

            foreach (WholeReadScanner.SlotEntry slot in entry.Slots)
            {
                if (_isV3)
                    RecordStorageV3(block, addrHash, slot.Slot, slot.Value, storageKey, storageValue, pending!, columns.StorageHistory);
                else
                    RecordStorage(block, addrHash, slot.Slot, slot.Value, storageKey, storageValue, in columns);
            }
        }
    }

    private void RecordAccount(ulong block, in ValueHash256 addrHash, Account? account, Span<byte> keyBuffer, scoped in HistoryColumnBatches columns)
    {
        ReadOnlySpan<byte> flatKey = BaseFlatPersistence.EncodeAccountKeyHashed(keyBuffer, addrHash);

        if (account is null)
        {
            _accountHistory!.RecordChange(block, flatKey, ReadOnlySpan<byte>.Empty, columns.AccountHistory);
            return;
        }

        using ArrayPoolSpan<byte> value = AccountDecoder.Slim.EncodeToArrayPoolSpan(account);
        _accountHistory!.RecordChange(block, flatKey, value, columns.AccountHistory);
    }

    private void RecordStorage(ulong block, in ValueHash256 addrHash, in UInt256 slot, in SlotValue? value, Span<byte> keyBuffer, Span<byte> valueBuffer, scoped in HistoryColumnBatches columns)
    {
        ValueHash256 slotHash = ValueKeccak.Zero;
        StorageTree.ComputeKeyWithLookup(slot, ref slotHash);
        ReadOnlySpan<byte> flatKey = BaseFlatPersistence.EncodeStorageKeyHashedWithShortPrefix(keyBuffer, addrHash, slotHash);

        // A removed slot, or one stripped to empty (zero), is a tombstone — matching the flat column,
        // which removes / stores an empty value in the same cases.
        int written = value is SlotValue slotValue
            ? BaseFlatPersistence.EncodeSlotValue(slotValue, _rlpWrapSlots, valueBuffer)
            : 0;
        _storageHistory!.RecordChange(block, flatKey, valueBuffer[..written], columns.StorageHistory);
    }

    /// <summary>
    /// v3 write: <paramref name="pending"/> tracks, per key, the newest block within this same walk whose
    /// "value before that block" is still unknown. Since the walk visits blocks newest-to-oldest, the post-value
    /// this call observes at <paramref name="block"/> is exactly the value the key held right before whatever
    /// newer touch is still pending for it — that pending row can now be finalized. This call's own touch then
    /// becomes the new pending entry, resolved either by an even older touch later in the same walk, or by
    /// <see cref="ResolvePendingV3"/> once the walk connects.
    /// </summary>
    private void RecordAccountV3(ulong block, in ValueHash256 addrHash, Account? account, Span<byte> keyBuffer, PendingV3Writes pending, IWriteBatch accountBatch)
    {
        byte[] flatKey = BaseFlatPersistence.EncodeAccountKeyHashed(keyBuffer, addrHash).ToArray();
        byte[] postValue = EncodeAccountBytes(account);
        pending.ResolveAndTrack(pending.Accounts, flatKey, block, postValue, accountBatch, _accountHistoryV3!);
    }

    private void RecordStorageV3(ulong block, in ValueHash256 addrHash, in UInt256 slot, in SlotValue? value, Span<byte> keyBuffer, Span<byte> valueBuffer, PendingV3Writes pending, IWriteBatch storageBatch)
    {
        ValueHash256 slotHash = ValueKeccak.Zero;
        StorageTree.ComputeKeyWithLookup(slot, ref slotHash);
        byte[] flatKey = BaseFlatPersistence.EncodeStorageKeyHashedWithShortPrefix(keyBuffer, addrHash, slotHash).ToArray();

        int written = value is SlotValue slotValue
            ? BaseFlatPersistence.EncodeSlotValue(slotValue, _rlpWrapSlots, valueBuffer)
            : 0;
        byte[] postValue = valueBuffer[..written].ToArray();
        pending.ResolveAndTrack(pending.Storages, flatKey, block, postValue, storageBatch, _storageHistoryV3!);
    }

    /// <summary>
    /// v3 only: a self-destruct with persisted storage wipes every slot without leaving a per-slot changeset entry
    /// (the live column expresses it as a range-delete), so those slots are recovered here by enumerating the
    /// account's persisted slots and feeding each one through the same deferred pre-value mechanism as an ordinary
    /// write, with an empty post-value (the wipe). Reading the persisted flat column is safe: capture always runs
    /// strictly before this round's flat persist commits — see the class-level remarks and
    /// <see cref="HistoryStoreV3"/>'s remarks for the invariant chain this relies on. A same-key touch anywhere
    /// else in this walk — an older write establishing the true pre-destruct value, or a same-block rewrite
    /// (resurrection) — chains against this synthetic touch exactly as it would against a real one;
    /// <see cref="PendingV3Writes.ResolveAndTrack"/>'s same-block guard keeps the latter from corrupting either
    /// entry. Above <see cref="DestructSlotEnumerationCap"/> slots, gives up and poisons the account for this
    /// block instead: <see cref="HistoryReader"/>'s v3 storage path fails closed for it rather than silently
    /// missing rows.
    /// </summary>
    private void HandleSelfDestructV3(ulong block, in ValueHash256 addrHash, scoped ReadOnlySpan<byte> accountKey, PendingV3Writes pending, IWriteBatch storageBatch, IWriteBatch clearsBatch)
    {
        using IPersistence.IFlatIterator slots = _persistedFlatReader.CreateStorageIterator(addrHash, ValueKeccak.Zero, ValueKeccak.MaxValue);
        Span<byte> storageKeyBuffer = stackalloc byte[BaseFlatPersistence.StorageKeyLength];
        int slotCount = 0;
        while (slots.MoveNext())
        {
            if (++slotCount > DestructSlotEnumerationCap)
            {
                _storageClears.RecordPoisonedClear(block, accountKey, clearsBatch);
                return;
            }

            byte[] flatKey = BaseFlatPersistence.EncodeStorageKeyHashedWithShortPrefix(storageKeyBuffer, addrHash, slots.CurrentKey).ToArray();
            pending.ResolveAndTrack(pending.Storages, flatKey, block, ReadOnlySpan<byte>.Empty, storageBatch, _storageHistoryV3!);
        }
    }

    private static byte[] EncodeAccountBytes(Account? account)
    {
        if (account is null) return [];
        using ArrayPoolSpan<byte> rlp = AccountDecoder.Slim.EncodeToArrayPoolSpan(account);
        return ((ReadOnlySpan<byte>)rlp).ToArray();
    }

    /// <summary>
    /// Finalizes every key touched during this walk whose oldest (within the walk) pre-value is still unknown —
    /// there was no even-older touch of it later in the same walk to resolve it. The persisted flat column is the
    /// correct source for "value right before this block's change" here: capture always runs strictly before
    /// this round's flat persist commits, so at this point the persisted column still reflects exactly the state
    /// as of the old watermark (before this batch), and nothing between the old watermark and this pending
    /// block touched the key — otherwise that touch would already have resolved this entry.
    /// </summary>
    private void ResolvePendingV3(PendingV3Writes pending)
    {
        if (pending.Accounts.Count == 0 && pending.Storages.Count == 0) return;

        using IColumnsWriteBatch<FlatHistoryColumns> batch = _history.StartWriteBatch();
        IWriteBatch accountBatch = batch.GetColumnBatch(FlatHistoryColumns.AccountHistory);
        IWriteBatch storageBatch = batch.GetColumnBatch(FlatHistoryColumns.StorageHistory);

        Span<byte> valueBuffer = stackalloc byte[512];
        foreach ((byte[] flatKey, ulong block) in pending.Accounts.Values)
        {
            int written = _persistedAccounts.Get(flatKey, valueBuffer);
            _accountHistoryV3!.RecordPreValue(block, flatKey, valueBuffer[..Math.Max(written, 0)], accountBatch);
        }

        foreach ((byte[] flatKey, ulong block) in pending.Storages.Values)
        {
            int written = _persistedStorage.Get(flatKey, valueBuffer);
            _storageHistoryV3!.RecordPreValue(block, flatKey, valueBuffer[..Math.Max(written, 0)], storageBatch);
        }
    }

    /// <summary>Per-walk deferred-resolution state for v3 capture — see <see cref="RecordAccountV3"/>.</summary>
    private sealed class PendingV3Writes
    {
        public readonly Dictionary<string, (byte[] FlatKey, ulong Block)> Accounts = [];
        public readonly Dictionary<string, (byte[] FlatKey, ulong Block)> Storages = [];

        public void ResolveAndTrack(
            Dictionary<string, (byte[] FlatKey, ulong Block)> map,
            byte[] flatKey,
            ulong block,
            ReadOnlySpan<byte> postValue,
            IWriteBatch batch,
            HistoryStoreV3 store)
        {
            string keyHex = Convert.ToHexString(flatKey);
            if (map.TryGetValue(keyHex, out (byte[] FlatKey, ulong Block) olderPending))
            {
                if (olderPending.Block == block)
                {
                    // A second touch of the same key within the same block - e.g. a self-destruct's synthetic
                    // wipe (HandleSelfDestructV3) and an explicit rewrite of the same slot in the same block
                    // (resurrection). Both represent the one externally-visible change at this block; there is no
                    // "older" entry here to resolve, and firing RecordPreValue between them would fabricate a
                    // pre-value from whichever call happens to run second. Leave the pending entry pointed at this
                    // block - an even older touch (elsewhere in the walk) or the persisted-flat fallback still
                    // resolves it correctly regardless of which of the two calls ran first.
                    return;
                }

                store.RecordPreValue(olderPending.Block, olderPending.FlatKey, postValue, batch);
            }

            map[keyHex] = (flatKey, block);
        }
    }

    private readonly ref struct HistoryColumnBatches(IColumnsWriteBatch<FlatHistoryColumns> batch)
    {
        public readonly IWriteBatch AccountHistory = batch.GetColumnBatch(FlatHistoryColumns.AccountHistory);
        public readonly IWriteBatch StorageHistory = batch.GetColumnBatch(FlatHistoryColumns.StorageHistory);
        public readonly IWriteBatch StorageClears = batch.GetColumnBatch(FlatHistoryColumns.StorageClears);
        public readonly IWriteBatch AvailableBlocks = batch.GetColumnBatch(FlatHistoryColumns.AvailableBlocks);
    }
}
