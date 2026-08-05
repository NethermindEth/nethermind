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
public sealed class HistoryWriter : IFlatPersistenceCaptureHook, IStateHistoryCaptureStatus
{
    // A capture failing forever would stall persistence and grow the in-memory tier until OOM; degrade instead.
    private const int MaxConsecutiveCaptureFailures = 16;

    private readonly IColumnsDb<FlatHistoryColumns> _history;
    private readonly HistoryStore _accountHistory;
    private readonly HistoryStore _storageHistory;
    private readonly StorageClearStore _storageClears;
    private readonly ChangesetSidecarStore? _changesetSidecar;
    private readonly HistoryAvailability _availability;
    private readonly bool _rlpWrapSlots;
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
        _accountHistory = new HistoryStore(history.GetColumnDb(FlatHistoryColumns.AccountHistory), logger);
        _storageHistory = new HistoryStore(history.GetColumnDb(FlatHistoryColumns.StorageHistory), logger);
        _storageClears = new StorageClearStore(history.GetColumnDb(FlatHistoryColumns.StorageClears));
        _changesetSidecar = config.HistoryChangesetSidecarEnabled
            ? new ChangesetSidecarStore(history.GetColumnDb(FlatHistoryColumns.ChangesetSidecar))
            : null;
        _availability = new HistoryAvailability(history.GetColumnDb(FlatHistoryColumns.AvailableBlocks));
        bool alreadyWindowedOnDisk = _availability.StampedFormatVersion == HistoryAvailability.WindowedFormatVersion;
        _formatVersion = config.HistoryRetentionBlocks > 0 || alreadyWindowedOnDisk
            ? HistoryAvailability.WindowedFormatVersion
            : HistoryAvailability.FormatVersion;
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
                    CaptureBlock(current.BlockNumber, current.StateRoot, snapshot);
                    current = snapshot.From;
                }
            }
            else if (snapshotRepository.TryLeaseBasePersistedSnapshot(current, out PersistedSnapshot? persisted))
            {
                using (persisted)
                {
                    CaptureBlock(current.BlockNumber, current.StateRoot, persisted);
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
    /// <remarks>Must run at startup before block processing: it writes without the persistence lock that
    /// serializes <see cref="CaptureUpTo"/>, so it must not overlap a capture.</remarks>
    [SkipLocalsInit]
    public void SeedGenesis(IReadOnlyCollection<KeyValuePair<Address, Account>> allocations, in ValueHash256 genesisStateRoot)
    {
        if (!_enabled) return;

        using (IColumnsWriteBatch<FlatHistoryColumns> batch = _history.StartWriteBatch())
        {
            HistoryColumnBatches columns = new(batch);
            HistoryAvailability.MarkBlock(columns.AvailableBlocks, 0, genesisStateRoot, _formatVersion);

            Span<byte> accountKey = stackalloc byte[BaseFlatPersistence.AccountKeyLength];
            foreach (KeyValuePair<Address, Account> allocation in allocations)
            {
                RecordAccount(0, allocation.Key.ToAccountPath, allocation.Value, accountKey, in columns);
            }
        }

        // The genesis floor a later walk connects to. Deliberately not proof of health: the nodes that reach the
        // seed (history enabled mid-life) are exactly the ones whose first walk fails.
        _availability.PublishWatermark(0, _formatVersion);
        _history.SyncWal();
    }

    [SkipLocalsInit]
    private void CaptureBlock(ulong block, in ValueHash256 stateRoot, Snapshot snapshot)
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

            _storageClears.RecordClear(block, BaseFlatPersistence.EncodeAccountKeyHashed(accountKey, destructed.Key.Key.ToAccountPath), columns.StorageClears);
        }

        foreach (KeyValuePair<HashedKey<Address>, Account?> change in snapshot.Accounts)
        {
            RecordAccount(block, change.Key.Key.ToAccountPath, change.Value, accountKey, in columns);
        }

        Span<byte> storageKey = stackalloc byte[BaseFlatPersistence.StorageKeyLength];
        Span<byte> storageValue = stackalloc byte[BaseFlatPersistence.RlpSlotValueBufferSize];
        foreach (KeyValuePair<HashedKey<(Address, UInt256)>, SlotValue?> change in snapshot.Storages)
        {
            (Address addr, UInt256 slot) = change.Key.Key;
            RecordStorage(block, addr.ToAccountPath, slot, change.Value, storageKey, storageValue, in columns);
        }

        if (_changesetSidecar is not null)
        {
            RecordChangesetSidecarChunk(block, snapshot, batch);
        }
    }

    /// <summary>
    /// Writes the block's changeset into the sidecar as a single chunk, grouped by address (BAL-shaped). Splitting
    /// a block's changeset across multiple chunks when it is too large for one wire message is a 39-2 concern —
    /// this establishes the store/codec shape and chunk-index contract, not the splitting policy.
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
        _changesetSidecar!.RecordChunk(block, chunkIndex: 0, payload, batch.GetColumnBatch(FlatHistoryColumns.ChangesetSidecar));
    }

    /// <summary>
    /// Captures a block whose in-memory base was converted to the persisted tier by long-finality Phase 2 — the
    /// persisted base holds the same one-block changeset.
    /// </summary>
    [SkipLocalsInit]
    private void CaptureBlock(ulong block, in ValueHash256 stateRoot, PersistedSnapshot snapshot)
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
                _storageClears.RecordClear(block, BaseFlatPersistence.EncodeAccountKeyHashed(accountKey, addrHash), columns.StorageClears);
            }

            if (entry.HasAccount)
            {
                RecordAccount(block, addrHash, entry.Account, accountKey, in columns);
            }

            foreach (WholeReadScanner.SlotEntry slot in entry.Slots)
            {
                RecordStorage(block, addrHash, slot.Slot, slot.Value, storageKey, storageValue, in columns);
            }
        }
    }

    private void RecordAccount(ulong block, in ValueHash256 addrHash, Account? account, Span<byte> keyBuffer, scoped in HistoryColumnBatches columns)
    {
        ReadOnlySpan<byte> flatKey = BaseFlatPersistence.EncodeAccountKeyHashed(keyBuffer, addrHash);

        if (account is null)
        {
            _accountHistory.RecordChange(block, flatKey, ReadOnlySpan<byte>.Empty, columns.AccountHistory);
            return;
        }

        using ArrayPoolSpan<byte> value = AccountDecoder.Slim.EncodeToArrayPoolSpan(account);
        _accountHistory.RecordChange(block, flatKey, value, columns.AccountHistory);
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
        _storageHistory.RecordChange(block, flatKey, valueBuffer[..written], columns.StorageHistory);
    }

    private readonly ref struct HistoryColumnBatches(IColumnsWriteBatch<FlatHistoryColumns> batch)
    {
        public readonly IWriteBatch AccountHistory = batch.GetColumnBatch(FlatHistoryColumns.AccountHistory);
        public readonly IWriteBatch StorageHistory = batch.GetColumnBatch(FlatHistoryColumns.StorageHistory);
        public readonly IWriteBatch StorageClears = batch.GetColumnBatch(FlatHistoryColumns.StorageClears);
        public readonly IWriteBatch AvailableBlocks = batch.GetColumnBatch(FlatHistoryColumns.AvailableBlocks);
    }
}
