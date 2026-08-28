// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
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
public sealed class HistoryWriter : IFlatPersistenceCaptureHook, IStateHistoryCaptureStatus, IHistoryPivotSeeder
{
    // A capture failing forever would stall persistence and grow the in-memory tier until OOM; degrade instead.
    private const int MaxConsecutiveCaptureFailures = 16;

    // Above this, materializing a pre-value row per wiped slot is unbounded work on one block; poison instead.
    internal const int DestructSlotEnumerationCap = 10_000;


    private readonly IColumnsDb<FlatHistoryColumns> _history;
    private readonly HistoryStore? _accountHistory;
    private readonly HistoryStore? _storageHistory;
    private readonly HistoryStoreV3? _accountHistoryV3;
    private readonly HistoryStoreV3? _storageHistoryV3;
    // Safe to read during capture: capture always runs before this round's flat persist commits.
    private readonly IDb _persistedAccounts;
    private readonly IDb _persistedStorage;
    private readonly BaseFlatPersistence.Reader _persistedFlatReader;
    private readonly StorageClearStore _storageClears;
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

    private readonly byte _formatVersion;
    private readonly PendingV3Writes? _pendingV3;
    private bool _formatStamped;

    public HistoryWriter(IColumnsDb<FlatDbColumns> db, IColumnsDb<FlatHistoryColumns> history, IFlatDbConfig config, HistoryAvailability availability, HistoryRowFormat rowFormat, ILogManager logManager)
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
        _availability = availability;
        _formatVersion = rowFormat.FormatVersion;
        _formatStamped = availability.StampedFormatVersion == _formatVersion;
        _isV3 = rowFormat.IsV3;
        _pendingV3 = _isV3 ? new PendingV3Writes() : null;
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

        // v3 only: oldest touch per key still waiting to learn its pre-value.
        PendingV3Writes? pending = _pendingV3;
        pending?.Clear();

        StateId current = persistedHead;
        bool connected = false;

        // Rows are written in descending block order, so a key's upper row must not become visible before the
        // lower one ResolvePendingV3 writes. Holds only while every history write stays WriteFlags.None.
        // Not `connected`: ResolvePendingV3 writes into the same batch after the walk connects.
        bool complete = false;
        IColumnsWriteBatch<FlatHistoryColumns> walkBatch = _history.StartWriteBatch();
        try
        {
            HistoryColumnBatches columns = new(walkBatch);
            connected = WalkAndCapture(ref current, hasWatermark, watermark, snapshotRepository, pending, in columns, cancellationToken);

            if (connected && pending is not null) ResolvePendingV3(pending, in columns);
            complete = connected;
        }
        finally
        {
            // Disposing writes, and a refusal disables capture, so nothing would rewrite a half-published walk.
            if (!complete) walkBatch.Clear();
            walkBatch.Dispose();
        }

        if (connected)
        {
            // The rows are visible from the dispose above; readers must learn that before the caller supersedes the
            // live column, so this stays ahead of everything else here.
            _availability.MarkCapturePublished();

            // Durable (throwing WAL-sync) before the caller persists the flat state and prunes the sources.
            _availability.PublishWatermark(target, _formatVersion);
            _history.SyncWal();
            _formatStamped = true;
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
            ReportUnconnectedWalk(current, hasWatermark, watermark);
        }
    }

    /// <summary>Returns whether it connected.</summary>
    private bool WalkAndCapture(
        ref StateId current,
        bool hasWatermark,
        ulong watermark,
        ISnapshotRepository snapshotRepository,
        PendingV3Writes? pending,
        in HistoryColumnBatches columns,
        CancellationToken cancellationToken)
    {
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
                    return false;
                }

                return true;
            }

            if (snapshotRepository.TryLeaseInMemoryState(current, SnapshotTier.InMemoryBase, out Snapshot? snapshot))
            {
                using (snapshot)
                {
                    CaptureBlock(current.BlockNumber, current.StateRoot, snapshot, pending, in columns);
                    current = snapshot.From;
                }
            }
            else if (snapshotRepository.TryLeaseBasePersistedSnapshot(current, out PersistedSnapshot? persisted))
            {
                using (persisted)
                {
                    CaptureBlock(current.BlockNumber, current.StateRoot, persisted, pending, in columns);
                    current = persisted.From;
                }
            }
            else
            {
                break;
            }
        }

        return current == StateId.PreGenesis;
    }

    /// <summary>Only reachable when history was enabled mid-life, so it is permanent.</summary>
    private void ReportUnconnectedWalk(in StateId current, bool hasWatermark, ulong watermark) =>
        DisableCapture($"History capture stopped at {current} without connecting to the captured range - " +
            $"the blocks below were pruned before history was enabled. The watermark stays at " +
            $"{(hasWatermark ? watermark.ToString() : "none")}; as-of reads above it report no history, and capture is disabled until restart.");

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
    /// <remarks>v3 writes no rows (the persisted-flat fallback answers); v2 must write explicit rows. Runs at
    /// startup before block processing - it writes without the persistence lock.</remarks>
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
                foreach (KeyValuePair<Address, Account> allocation in allocations)
                {
                    RecordAccount(0, allocation.Key.ToAccountPath, allocation.Value, in columns);
                }
            }
        }

        // The genesis floor a later walk connects to. Deliberately not proof of health: the nodes that reach the
        // seed (history enabled mid-life) are exactly the ones whose first walk fails.
        _availability.PublishWatermark(0, _formatVersion);
        _history.SyncWal();
    }

    /// <summary>
    /// Seeds a windowed node's floor at a snap-sync pivot: publishes watermark = floor = pivot with no rows needed
    /// under v3 (the persisted flat column holds exactly the pivot's state; the fallback answers). No-op on v2,
    /// which has no fallback to lean on. Call at the sync-completion seam before any block processes on top.
    /// </summary>
    public void SeedPivot(ulong pivotBlock, in ValueHash256 pivotStateRoot)
    {
        if (!_enabled || !_isV3) return;

        // Raise-only: seeding a pivot below an already-published floor would re-admit reads for heights whose
        // rows the pruner has already deleted, and they would answer from live state instead of failing closed.
        if (_availability.TryGetGlobalFloor(out ulong currentFloor) && pivotBlock < currentFloor)
        {
            throw new InvalidOperationException(
                $"Cannot seed the flat history floor at pivot {pivotBlock}: it is below the published retention floor " +
                $"{currentFloor}, whose rows are already pruned. Resync the flatHistory database to start from this pivot.");
        }

        if (_availability.TryGetWatermark(out ulong currentWatermark) && pivotBlock < currentWatermark)
        {
            throw new InvalidOperationException(
                $"Cannot seed the flat history floor at pivot {pivotBlock}: it is inside the already-captured window, " +
                $"whose watermark is {currentWatermark}. The snap sync replaced the live state the captured history above " +
                "the pivot resolves through, so its as-of reads would answer from the pivot's state instead of failing " +
                "closed. Resync the flatHistory database to start from this pivot.");
        }

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
    private void CaptureBlock(ulong block, in ValueHash256 stateRoot, Snapshot snapshot, PendingV3Writes? pending, in HistoryColumnBatches columns)
    {
        HistoryAvailability.MarkBlock(columns.AvailableBlocks, block, stateRoot, _formatVersion, stampFormat: !_formatStamped);

        foreach (KeyValuePair<HashedKey<Address>, bool> destructed in snapshot.SelfDestructedStorageAddresses)
        {
            // Value == true means the account had no persisted storage before the destruct; PersistenceManager
            // skips the flat range-delete in that case, so there is nothing in history to shadow either.
            if (destructed.Value) continue;

            ValueHash256 addrHash = destructed.Key.Key.ToAccountPath;
            _storageClears.RecordClear(block, addrHash.Bytes, columns.StorageClears);

            // Tracked before the enumeration, so an account that trips the cap still gets splices for its
            // in-walk slots. The reader answers from those rows; the poison only refuses reads that would
            // otherwise fall through to the live column.
            if (_isV3) pending!.TrackDestruct(addrHash, block);
        }

        foreach (KeyValuePair<HashedKey<Address>, Account?> change in snapshot.Accounts)
        {
            Address address = change.Key.Key;
            if (_isV3)
                RecordAccountV3(block, address.ToAccountPath, change.Value, pending!, columns.AccountHistory);
            else
                RecordAccount(block, address.ToAccountPath, change.Value, in columns);
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

        if (!_isV3) return;

        foreach (KeyValuePair<HashedKey<Address>, bool> destructed in snapshot.SelfDestructedStorageAddresses)
        {
            if (destructed.Value) continue;

            ValueHash256 addrHash = destructed.Key.Key.ToAccountPath;
            HandleSelfDestructV3(block, addrHash, addrHash.Bytes, pending!, columns.StorageHistory, columns.StorageClears);
        }
    }

    /// <summary>
    /// Captures a block whose in-memory base was converted to the persisted tier by long-finality Phase 2 — the
    /// persisted base holds the same one-block changeset.
    /// </summary>
    [SkipLocalsInit]
    private void CaptureBlock(ulong block, in ValueHash256 stateRoot, PersistedSnapshot snapshot, PendingV3Writes? pending, in HistoryColumnBatches columns)
    {
        using WholeReadSession session = snapshot.BeginWholeReadSession();
        WholeReadScanner scanner = PersistedSnapshotScanner.ForWholeRead(session, snapshot);

        HistoryAvailability.MarkBlock(columns.AvailableBlocks, block, stateRoot, _formatVersion, stampFormat: !_formatStamped);

        Span<byte> storageKey = stackalloc byte[BaseFlatPersistence.StorageKeyLength];
        Span<byte> storageValue = stackalloc byte[BaseFlatPersistence.RlpSlotValueBufferSize];
        foreach (WholeReadScanner.PerAddressEntry entry in scanner.PerAddresses)
        {
            ValueHash256 addrHash = entry.Address.ToAccountPath;

            if (entry.SelfDestructFlag is false)
            {
                _storageClears.RecordClear(block, addrHash.Bytes, columns.StorageClears);

                if (_isV3) pending!.TrackDestruct(addrHash, block);
            }

            if (entry.HasAccount)
            {
                if (_isV3)
                    RecordAccountV3(block, addrHash, entry.Account, pending!, columns.AccountHistory);
                else
                    RecordAccount(block, addrHash, entry.Account, in columns);
            }

            foreach (WholeReadScanner.SlotEntry slot in entry.Slots)
            {
                if (_isV3)
                {
                    RecordStorageV3(block, addrHash, slot.Slot, slot.Value, storageKey, storageValue, pending!, columns.StorageHistory);
                }
                else
                {
                    RecordStorage(block, addrHash, slot.Slot, slot.Value, storageKey, storageValue, in columns);
                }
            }

            if (_isV3 && entry.SelfDestructFlag is false)
            {
                HandleSelfDestructV3(block, addrHash, addrHash.Bytes, pending!, columns.StorageHistory, columns.StorageClears);
            }
        }
    }

    private void RecordAccount(ulong block, in ValueHash256 addrHash, Account? account, scoped in HistoryColumnBatches columns)
    {
        ReadOnlySpan<byte> flatKey = addrHash.Bytes;

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

    /// <summary>The walk visits newest-to-oldest, so this post-value finalizes the newer pending touch.</summary>
    private void RecordAccountV3(ulong block, in ValueHash256 addrHash, Account? account, PendingV3Writes pending, IWriteBatch accountBatch)
    {
        if (account is null)
        {
            pending.TrackAccount(addrHash, block, ReadOnlySpan<byte>.Empty, accountBatch, _accountHistoryV3!);
            return;
        }

        using ArrayPoolSpan<byte> rlp = AccountDecoder.Slim.EncodeToArrayPoolSpan(account);
        pending.TrackAccount(addrHash, block, rlp, accountBatch, _accountHistoryV3!);
    }

    private void RecordStorageV3(ulong block, in ValueHash256 addrHash, in UInt256 slot, in SlotValue? value, Span<byte> keyBuffer, Span<byte> valueBuffer, PendingV3Writes pending, IWriteBatch storageBatch)
    {
        ValueHash256 slotHash = ValueKeccak.Zero;
        StorageTree.ComputeKeyWithLookup(slot, ref slotHash);

        int written = value is SlotValue slotValue
            ? BaseFlatPersistence.EncodeSlotValue(slotValue, _rlpWrapSlots, valueBuffer)
            : 0;
        pending.TrackStorage(addrHash, slotHash, block, valueBuffer[..written], keyBuffer, storageBatch, _storageHistoryV3!);
    }

    /// <summary>v3 only: a self-destruct wipes slots via a range-delete with no per-slot entries, so the account's
    /// persisted slots are enumerated and fed through the pending mechanism as synthetic empty-post-value touches.
    /// Slots first written lower in this same walk are absent from the persisted column and so invisible here;
    /// <see cref="PendingV3Writes.TrackDestruct"/> covers them when the walk reaches them.
    /// Above <see cref="DestructSlotEnumerationCap"/> slots, poisons the account for this block instead; the read
    /// path fails closed for it.</summary>
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
                Metrics.FlatHistoryPoisonedDestructs++;
                if (_logger.IsWarn) _logger.Warn(
                    $"Account {addrHash} self-destructed at block {block} with more than {DestructSlotEnumerationCap} persisted slots; " +
                    "its per-slot pre-values were not recorded, so storage reads for it below this block will fail closed.");
                return;
            }

            pending.TrackStorage(addrHash, slots.CurrentKey, block, ReadOnlySpan<byte>.Empty, storageKeyBuffer, storageBatch, _storageHistoryV3!);
        }
    }

    private const int PreValueMultiGetChunkSize = 1024;

    /// <summary>Resolves the oldest touches from the persisted flat column, which still holds pre-walk values.
    /// A walk-local destruct below a pending touch is spliced in - the counterpart of
    /// <see cref="PendingV3Writes.TrackStorage"/>'s in-walk splice, needing no lower bound because the pending
    /// touch is the walk's lowest for the key.</summary>
    private void ResolvePendingV3(PendingV3Writes pending, in HistoryColumnBatches columns)
    {
        if (pending.Accounts.Count == 0 && pending.Storages.Count == 0) return;

        IWriteBatch accountBatch = columns.AccountHistory;
        IWriteBatch storageBatch = columns.StorageHistory;

        Span<byte> keyBuffer = stackalloc byte[BaseFlatPersistence.StorageKeyLength];
        KeyValuePair<ValueHash256, ulong>[] accounts = SortedAccounts(pending.Accounts);
        for (int offset = 0; offset < accounts.Length; offset += PreValueMultiGetChunkSize)
        {
            int count = Math.Min(PreValueMultiGetChunkSize, accounts.Length - offset);
            byte[][] keys = new byte[count][];
            for (int i = 0; i < count; i++)
            {
                keys[i] = HistoryKeyLayout.ToFlatStateKey(accounts[offset + i].Key.Bytes).ToArray();
            }

            KeyValuePair<byte[], byte[]?>[] preValues = _persistedAccounts[keys];
            for (int i = 0; i < count; i++)
            {
                _accountHistoryV3!.RecordPreValue(accounts[offset + i].Value, accounts[offset + i].Key.Bytes, preValues[i].Value ?? ReadOnlySpan<byte>.Empty, accountBatch);
            }
        }

        KeyValuePair<PendingV3Writes.SlotKey, ulong>[] storages = SortedStorages(pending.Storages);
        for (int offset = 0; offset < storages.Length; offset += PreValueMultiGetChunkSize)
        {
            int count = Math.Min(PreValueMultiGetChunkSize, storages.Length - offset);
            byte[][] keys = new byte[count][];
            for (int i = 0; i < count; i++)
            {
                keys[i] = BaseFlatPersistence.EncodeStorageKeyHashedWithShortPrefix(keyBuffer, storages[offset + i].Key.AddrPath, storages[offset + i].Key.SlotHash).ToArray();
            }

            KeyValuePair<byte[], byte[]?>[] preValues = _persistedStorage[keys];
            for (int i = 0; i < count; i++)
            {
                ulong touch = storages[offset + i].Value;
                ReadOnlySpan<byte> preValue = preValues[i].Value ?? ReadOnlySpan<byte>.Empty;

                if (pending.Destructs.Count != 0
                    && pending.Destructs.TryGetValue(storages[offset + i].Key.AddrPath, out ulong destructBlock)
                    && destructBlock < touch)
                {
                    _storageHistoryV3!.RecordPreValue(destructBlock, keys[i], preValue, storageBatch);
                    preValue = ReadOnlySpan<byte>.Empty;
                }

                _storageHistoryV3!.RecordPreValue(touch, keys[i], preValue, storageBatch);
            }
        }
    }

    // Sorted so each multi-get hands the persisted column keys in key order (grouped per account for storage),
    // sharing index and data blocks instead of hitting them randomly per chunk.
    private static KeyValuePair<ValueHash256, ulong>[] SortedAccounts(Dictionary<ValueHash256, ulong> map)
    {
        KeyValuePair<ValueHash256, ulong>[] entries = new KeyValuePair<ValueHash256, ulong>[map.Count];
        ((ICollection<KeyValuePair<ValueHash256, ulong>>)map).CopyTo(entries, 0);
        Array.Sort(entries, static (a, b) => a.Key.Bytes.SequenceCompareTo(b.Key.Bytes));
        return entries;
    }

    private static KeyValuePair<PendingV3Writes.SlotKey, ulong>[] SortedStorages(Dictionary<PendingV3Writes.SlotKey, ulong> map)
    {
        KeyValuePair<PendingV3Writes.SlotKey, ulong>[] entries = new KeyValuePair<PendingV3Writes.SlotKey, ulong>[map.Count];
        ((ICollection<KeyValuePair<PendingV3Writes.SlotKey, ulong>>)map).CopyTo(entries, 0);
        Array.Sort(entries, static (a, b) =>
        {
            int byAccount = a.Key.AddrPath.Bytes.SequenceCompareTo(b.Key.AddrPath.Bytes);
            return byAccount != 0 ? byAccount : a.Key.SlotHash.Bytes.SequenceCompareTo(b.Key.SlotHash.Bytes);
        });
        return entries;
    }

    /// <summary>Per-walk deferred-resolution state. Keyed on value structs so tracking a touch allocates nothing.</summary>
    private sealed class PendingV3Writes
    {
        public readonly record struct SlotKey(ValueHash256 AddrPath, ValueHash256 SlotHash);

        public readonly Dictionary<ValueHash256, ulong> Accounts = [];
        public readonly Dictionary<SlotKey, ulong> Storages = [];
        public readonly Dictionary<ValueHash256, ulong> Destructs = [];

        public void Clear()
        {
            Accounts.Clear();
            Storages.Clear();
            Destructs.Clear();
        }

        /// <summary>Keeps the lowest destruct seen, for slots first written lower in this same walk.</summary>
        public void TrackDestruct(in ValueHash256 addrPath, ulong block) => Destructs[addrPath] = block;

        public void TrackAccount(in ValueHash256 addrPath, ulong block, ReadOnlySpan<byte> postValue, IWriteBatch batch, HistoryStoreV3 store)
        {
            ref ulong entry = ref CollectionsMarshal.GetValueRefOrAddDefault(Accounts, addrPath, out bool exists);
            if (exists)
            {
                if (entry == block) return; // same-block re-touch: resolving here would fabricate a pre-value

                store.RecordPreValue(entry, addrPath.Bytes, postValue, batch);
            }

            entry = block;
        }

        public void TrackStorage(
            in ValueHash256 addrPath,
            in ValueHash256 slotHash,
            ulong block,
            ReadOnlySpan<byte> postValue,
            Span<byte> keyBuffer,
            IWriteBatch batch,
            HistoryStoreV3 store)
        {
            ref ulong entry = ref CollectionsMarshal.GetValueRefOrAddDefault(Storages, new SlotKey(addrPath, slotHash), out bool exists);
            if (exists && entry == block) return; // destruct wipe + same-block rewrite: leave the entry pending

            // A destruct standing between this touch and the next one up wiped the slot without enumerating it,
            // the slot having been absent from the persisted column at the time. Splice it into the chain: this
            // touch's post-value is what the destruct erased, and the next touch up therefore starts from empty.
            if (Destructs.Count != 0
                && Destructs.TryGetValue(addrPath, out ulong destructBlock)
                && destructBlock > block
                && (!exists || destructBlock < entry))
            {
                ReadOnlySpan<byte> destructKey = BaseFlatPersistence.EncodeStorageKeyHashedWithShortPrefix(keyBuffer, addrPath, slotHash);
                if (exists) store.RecordPreValue(entry, destructKey, ReadOnlySpan<byte>.Empty, batch);
                store.RecordPreValue(destructBlock, destructKey, postValue, batch);
                entry = block;
                return;
            }

            if (exists)
            {
                ReadOnlySpan<byte> flatKey = BaseFlatPersistence.EncodeStorageKeyHashedWithShortPrefix(keyBuffer, addrPath, slotHash);
                store.RecordPreValue(entry, flatKey, postValue, batch);
            }

            entry = block;
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
