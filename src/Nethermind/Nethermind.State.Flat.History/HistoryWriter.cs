// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
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
public sealed class HistoryWriter : IFlatPersistenceCaptureHook
{
    private readonly IColumnsDb<FlatHistoryColumns> _history;
    private readonly HistoryStore _accountHistory;
    private readonly HistoryStore _storageHistory;
    private readonly StorageClearStore _storageClears;
    private readonly HistoryAvailability _availability;
    private readonly bool _rlpWrapSlots;
    private readonly bool _enabled;
    private readonly ILogger _logger;

    // Under the persistence lock a failed lease means the range below is gone for good (history enabled mid-life);
    // further captures would only write rows above a gap no read can cross, so skip them until restart.
    private bool _permanentGapDetected;

    public HistoryWriter(IColumnsDb<FlatDbColumns> db, IColumnsDb<FlatHistoryColumns> history, IFlatDbConfig config, ILogManager logManager)
        : this(history, BasePersistence.ResolveSlotEncoding(
            db,
            (ISortedKeyValueStore)db.GetColumnDb(FlatDbColumns.Storage),
            logManager.GetClassLogger<HistoryWriter>()), config.HistoryEnabled, logManager.GetClassLogger<HistoryWriter>())
    {
    }

    private HistoryWriter(IColumnsDb<FlatHistoryColumns> history, bool rlpWrapSlots, bool enabled, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(history);
        _enabled = enabled;
        _history = history;
        _rlpWrapSlots = rlpWrapSlots;
        _logger = logger;
        _accountHistory = new HistoryStore(history.GetColumnDb(FlatHistoryColumns.AccountHistory));
        _storageHistory = new HistoryStore(history.GetColumnDb(FlatHistoryColumns.StorageHistory));
        _storageClears = new StorageClearStore(history.GetColumnDb(FlatHistoryColumns.StorageClears));
        _availability = new HistoryAvailability(history.GetColumnDb(FlatHistoryColumns.AvailableBlocks));
        if (enabled) _availability.VerifyFormat();
    }

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
    public void CaptureUpTo(in StateId persistedHead, ISnapshotRepository snapshotRepository)
    {
        if (!_enabled || _permanentGapDetected) return;

        ulong target = persistedHead.BlockNumber;
        bool hasWatermark = _availability.TryGetWatermark(out ulong watermark);
        if (hasWatermark && target <= watermark) return;

        StateId current = persistedHead;
        bool connected = false;
        while (current != StateId.PreGenesis)
        {
            if (hasWatermark && current.BlockNumber <= watermark)
            {
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
            // Publish, then WAL-sync so range + watermark are durable before the caller persists the flat state.
            _availability.PublishWatermark(target);
            _history.Flush(onlyWal: true);
        }
        else
        {
            // With capture ordered before every persist/prune, an unconnectable walk only happens when history was
            // enabled mid-life — permanent, so stop capturing instead of stalling or rewriting dead rows.
            _permanentGapDetected = true;
            if (_logger.IsWarn)
                _logger.Warn($"History capture stopped at {current} without connecting to the captured range - " +
                    $"the blocks below were pruned before history was enabled. The watermark stays at " +
                    $"{(hasWatermark ? watermark.ToString() : "none")}; as-of reads above it report no history, and capture is disabled until restart.");
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
            HistoryAvailability.MarkBlock(columns.AvailableBlocks, 0, genesisStateRoot);

            Span<byte> accountKey = stackalloc byte[BaseFlatPersistence.AccountKeyLength];
            foreach (KeyValuePair<Address, Account> allocation in allocations)
            {
                RecordAccount(0, allocation.Key.ToAccountPath, allocation.Value, accountKey, in columns);
            }
        }

        // Publish only after the block-0 batch is durable — this is the genesis floor a later walk connects to.
        _availability.PublishWatermark(0);
        _history.Flush(onlyWal: true);
    }

    [SkipLocalsInit]
    private void CaptureBlock(ulong block, in ValueHash256 stateRoot, Snapshot snapshot)
    {
        using IColumnsWriteBatch<FlatHistoryColumns> batch = _history.StartWriteBatch();
        HistoryColumnBatches columns = new(batch);
        HistoryAvailability.MarkBlock(columns.AvailableBlocks, block, stateRoot);

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
        HistoryAvailability.MarkBlock(columns.AvailableBlocks, block, stateRoot);

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
