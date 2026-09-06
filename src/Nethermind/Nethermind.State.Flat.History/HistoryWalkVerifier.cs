// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Flat.Persistence;
using Nethermind.Trie.Pruning;

namespace Nethermind.State.Flat.History;

/// <summary>
/// Proves an unwindowed (v2) archive's content against this node's own headers by rebuilding the state root from
/// rows at EVERY block of a range - per-block because a change attributed to the wrong block leaves the tip root
/// correct while every as-of answer in between is wrong. v2 only (v3 rows are pre-values behind a floor). Each
/// column is scanned ONCE and its rows routed to the owning segment, so segment count multiplies concurrency, not
/// IO. The working set is held in memory and is bounded by state size rather than by range span - each segment
/// starts from the whole account set as of its first block - so a request too large for the machine is refused
/// outright instead of being allowed to exhaust it.
/// </summary>
public sealed class HistoryWalkVerifier
{
    private const int BlockBytes = sizeof(ulong);
    private const int AccountRowKeyLength = HistoryKeyLayout.AccountKeyLength + BlockBytes;
    private const int StorageRowKeyLength = BaseFlatPersistence.StorageKeyLength + BlockBytes;
    private const int ClearRowKeyLength = HistoryKeyLayout.AccountKeyLength + BlockBytes;
    private const int IdentityLength = HistoryKeyLayout.ScopeKeyLength;
    private const int SlotPathOffset = BasePersistence.StoragePrefixPortion;
    private const int SlotSuffixOffset = SlotPathOffset + Hash256.Size;

    private readonly ISortedKeyValueStore _accountHistory;
    private readonly ISortedKeyValueStore _storageHistory;
    private readonly ISortedKeyValueStore _storageClears;
    private readonly IDb _availableBlocks;
    private readonly IHistoryHeaderSource _headers;
    private readonly HistoryRowFormat _rowFormat;
    private readonly bool _rlpWrapSlots;
    private readonly ILogManager _logManager;
    private readonly ILogger _logger;
    private readonly long _maxMaterializedRows;

    /// <summary>Ceiling on the rows one verification may hold at once, when the caller names none.</summary>
    public const long DefaultMaxMaterializedRows = 20_000_000;

    /// <summary>Resolves the slot encoding from the live flat database exactly the way <see cref="HistoryReader"/>
    /// does, so the verifier decodes slot rows with the same convention the writer stored them under.</summary>
    public HistoryWalkVerifier(
        IColumnsDb<FlatDbColumns> db,
        IColumnsDb<FlatHistoryColumns> history,
        IHistoryHeaderSource headers,
        HistoryRowFormat rowFormat,
        ILogManager logManager,
        long maxMaterializedRows = DefaultMaxMaterializedRows)
        : this(
            history,
            headers,
            rowFormat,
            BasePersistence.ResolveSlotEncoding(db, (ISortedKeyValueStore)db.GetColumnDb(FlatDbColumns.Storage), logManager.GetClassLogger<HistoryWalkVerifier>()),
            logManager,
            maxMaterializedRows)
    {
    }

    public HistoryWalkVerifier(
        IColumnsDb<FlatHistoryColumns> history,
        IHistoryHeaderSource headers,
        HistoryRowFormat rowFormat,
        bool rlpWrapSlots,
        ILogManager logManager,
        long maxMaterializedRows = DefaultMaxMaterializedRows)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(headers);
        ArgumentNullException.ThrowIfNull(rowFormat);
        ArgumentNullException.ThrowIfNull(logManager);

        if (rowFormat.IsV3)
        {
            throw new InvalidConfigurationException(
                "The every-block walk verifier only supports an unwindowed (v2) history: v2 rows are post-values a " +
                "forward walk can apply directly, while a windowed database stores pre-values, carries no rows at " +
                "all for unchanged keys, and has pruned the ancestry a genesis-anchored walk needs.", -1);
        }

        _accountHistory = (ISortedKeyValueStore)history.GetColumnDb(FlatHistoryColumns.AccountHistory);
        _storageHistory = (ISortedKeyValueStore)history.GetColumnDb(FlatHistoryColumns.StorageHistory);
        _storageClears = (ISortedKeyValueStore)history.GetColumnDb(FlatHistoryColumns.StorageClears);
        _availableBlocks = history.GetColumnDb(FlatHistoryColumns.AvailableBlocks);
        _headers = headers;
        _rowFormat = rowFormat;
        _rlpWrapSlots = rlpWrapSlots;
        _logManager = logManager;
        _logger = logManager.GetClassLogger<HistoryWalkVerifier>();
        _maxMaterializedRows = maxMaterializedRows > 0 ? maxMaterializedRows : DefaultMaxMaterializedRows;
    }

    /// <summary>Stops a verification before its partition exhausts the machine. The footprint tracks state size,
    /// not range span - every segment starts from the whole account set as of its first block - so narrowing the
    /// range is a weak lever and there is no chunking that makes an over-large request fit.</summary>
    private sealed class RowBudget(long max)
    {
        private long _materialized;

        public void Charge()
        {
            if (++_materialized <= max) return;

            throw new InvalidConfigurationException(
                $"History walk verification would have to hold more than {max} rows in memory for the requested " +
                "range. Verify a narrower range, or raise FlatDb.HistoryVerifyMaxRows if this machine has the " +
                "headroom - the verification is declined, not failed.", -1);
        }
    }

    // Per-segment slice of the partitioned scans: the state at the segment's start plus the deltas inside it.
    private sealed class SegmentData
    {
        public readonly List<(ValueHash256 Path, Account Account)> StartAccounts = [];
        public readonly SortedDictionary<ulong, List<(ValueHash256 Path, Account? Account)>> AccountDeltas = [];
        public readonly Dictionary<byte[], List<(ValueHash256 SlotPath, byte[] Value)>> StartSlots = new(Bytes.EqualityComparer);
        public readonly SortedDictionary<ulong, List<(byte[] Identity, ValueHash256 SlotPath, byte[] Value)>> SlotDeltas = [];
    }

    public HistoryWalkVerdict VerifyRange(ulong fromInclusive, ulong toInclusive, CancellationToken token) =>
        VerifyRangeParallel(fromInclusive, toInclusive, 1, token);

    /// <summary>Verifies the range as concurrent, independently anchored segments - each start state is compared
    /// to its own header before the walk, so no segment consumes another's output.</summary>
    public HistoryWalkVerdict VerifyRangeParallel(ulong fromInclusive, ulong toInclusive, int segments, CancellationToken token)
    {
        if (segments < 1) throw new ArgumentOutOfRangeException(nameof(segments));
        if (fromInclusive > toInclusive)
            throw new ArgumentException($"Range start {fromInclusive} is above its end {toInclusive}.", nameof(fromInclusive));

        ulong span = toInclusive - fromInclusive;
        int effectiveSegments = (int)Math.Min((ulong)segments, Math.Max(span, 1));
        (ulong From, ulong To)[] bounds = new (ulong, ulong)[effectiveSegments];
        for (int i = 0; i < effectiveSegments; i++)
        {
            bounds[i] = (
                fromInclusive + span * (ulong)i / (ulong)effectiveSegments,
                fromInclusive + span * ((ulong)i + 1) / (ulong)effectiveSegments);
        }

        SegmentData[] data = new SegmentData[effectiveSegments];
        for (int i = 0; i < effectiveSegments; i++) data[i] = new SegmentData();

        RowBudget budget = new(_maxMaterializedRows);
        Dictionary<byte[], List<ulong>> clearsByIdentity = ScanClears(bounds[^1].To, budget, token);
        PartitionAccounts(bounds, data, budget, token);
        PartitionStorage(bounds, data, clearsByIdentity, budget, token);

        HistoryWalkVerdict[] verdicts = new HistoryWalkVerdict[effectiveSegments];
        Parallel.For(0, effectiveSegments, new ParallelOptions { CancellationToken = token },
            i => verdicts[i] = WalkSegment(bounds[i].From, bounds[i].To, data[i], clearsByIdentity, countAnchor: i == 0, token));

        List<HistoryWalkMismatch> mismatches = [];
        ulong compared = 0;
        foreach (HistoryWalkVerdict verdict in verdicts)
        {
            compared += verdict.BlocksCompared;
            mismatches.AddRange(verdict.Mismatches);
        }

        mismatches.Sort(static (a, b) => a.Block.CompareTo(b.Block));
        return new HistoryWalkVerdict(mismatches.Count == 0, compared, mismatches);
    }

    private HistoryWalkVerdict WalkSegment(
        ulong fromInclusive,
        ulong toInclusive,
        SegmentData data,
        Dictionary<byte[], List<ulong>> clearsByIdentity,
        bool countAnchor,
        CancellationToken token)
    {
        List<HistoryWalkMismatch> mismatches = [];

        SortedDictionary<ulong, List<byte[]>> clearsByBlock = [];
        foreach ((byte[] identity, List<ulong> blocks) in clearsByIdentity)
        {
            foreach (ulong block in blocks)
            {
                if (block > fromInclusive && block <= toInclusive) GetOrAdd(clearsByBlock, block).Add(identity);
            }
        }

        StateTree state = new(new RawScopedTrieStore(new MemDb()), _logManager);
        Dictionary<byte[], ValueHash256> lastAccountStorageRoots = new(Bytes.EqualityComparer);
        foreach ((ValueHash256 path, Account account) in data.StartAccounts)
        {
            state.Set(path, account);
            lastAccountStorageRoots[path.Bytes[..IdentityLength].ToArray()] = new ValueHash256(account.StorageRoot.Bytes);
        }

        state.UpdateRootHash();

        // Adjacent segments share their boundary block: segment i's start is segment i-1's end. The check runs in
        // both (it is what anchors each segment), but only the first counts it, so BlocksCompared stays exact.
        ulong compared = 0;
        if (!CompareStateRoot(fromInclusive, state, mismatches, ref compared, countAnchor))
        {
            return new HistoryWalkVerdict(false, compared, mismatches);
        }

        Dictionary<byte[], StorageTree> storageTries = new(Bytes.EqualityComparer);

        for (ulong block = fromInclusive + 1; block <= toInclusive; block++)
        {
            token.ThrowIfCancellationRequested();

            HashSet<byte[]>? touched = null;
            if (clearsByBlock.TryGetValue(block, out List<byte[]>? clearedIdentities))
            {
                touched = new HashSet<byte[]>(Bytes.EqualityComparer);
                foreach (byte[] identity in clearedIdentities)
                {
                    storageTries[identity] = new StorageTree(new RawScopedTrieStore(new MemDb()), _logManager);
                    touched.Add(identity);
                }
            }

            if (data.SlotDeltas.TryGetValue(block, out List<(byte[] Identity, ValueHash256 SlotPath, byte[] Value)>? slots))
            {
                touched ??= new HashSet<byte[]>(Bytes.EqualityComparer);
                foreach ((byte[] identity, ValueHash256 slotPath, byte[] value) in slots)
                {
                    StorageTree tree = GetOrMaterialize(storageTries, data.StartSlots, identity);
                    tree.Set(slotPath, value, rlpEncode: !_rlpWrapSlots);
                    touched.Add(identity);
                }
            }

            Dictionary<byte[], Account?>? accountsAtBlock = null;
            data.AccountDeltas.TryGetValue(block, out List<(ValueHash256 Path, Account? Account)>? accountRows);
            if (accountRows is not null)
            {
                accountsAtBlock = new Dictionary<byte[], Account?>(Bytes.EqualityComparer);
                foreach ((ValueHash256 path, Account? account) in accountRows)
                {
                    accountsAtBlock[path.Bytes[..IdentityLength].ToArray()] = account;
                }
            }

            if (touched is not null)
            {
                foreach (byte[] identity in touched)
                {
                    StorageTree tree = storageTries[identity];
                    tree.UpdateRootHash();
                    ValueHash256 rebuiltStorageRoot = new(tree.RootHash.Bytes);

                    if (accountsAtBlock is null || !accountsAtBlock.TryGetValue(identity, out Account? owner))
                    {
                        // Every storage change moves the owner's storageRoot, so a block that changed a slot with
                        // no account row for its owner cannot have produced the header's state root honestly.
                        mismatches.Add(new HistoryWalkMismatch(block, HistoryWalkMismatchKind.MissingAccountRow, rebuiltStorageRoot, default));
                        continue;
                    }

                    if (owner is null) continue;

                    ValueHash256 recordedStorageRoot = new(owner.StorageRoot.Bytes);
                    if (rebuiltStorageRoot != recordedStorageRoot)
                    {
                        mismatches.Add(new HistoryWalkMismatch(block, HistoryWalkMismatchKind.StorageRoot, rebuiltStorageRoot, recordedStorageRoot));
                    }
                }
            }

            if (accountsAtBlock is not null)
            {
                foreach ((byte[] identity, Account? account) in accountsAtBlock)
                {
                    if (account is null)
                    {
                        lastAccountStorageRoots[identity] = new ValueHash256(Keccak.EmptyTreeHash.Bytes);
                        continue;
                    }

                    ValueHash256 recorded = new(account.StorageRoot.Bytes);
                    if (!lastAccountStorageRoots.TryGetValue(identity, out ValueHash256 previous))
                    {
                        previous = new ValueHash256(Keccak.EmptyTreeHash.Bytes);
                    }

                    if ((touched is null || !touched.Contains(identity)) && recorded != previous)
                    {
                        mismatches.Add(new HistoryWalkMismatch(block, HistoryWalkMismatchKind.MissingSlotHistory, previous, recorded));
                    }

                    lastAccountStorageRoots[identity] = recorded;
                }
            }

            if (accountRows is not null)
            {
                foreach ((ValueHash256 path, Account? account) in accountRows)
                {
                    state.Set(path, account);
                }
            }

            state.UpdateRootHash();
            if (!CompareStateRoot(block, state, mismatches, ref compared))
            {
                // Everything after a diverged state root is derivative noise; the block itself names the culprit.
                if (_logger.IsWarn) _logger.Warn($"History walk diverged from the header at block {block}; stopping this range.");
                break;
            }
        }

        bool verified = mismatches.Count == 0;
        return new HistoryWalkVerdict(verified, compared, mismatches);
    }

    /// <summary>Whether the walk can continue - a root or missing-header failure poisons every later comparison.
    /// Also checks the captured marker against the header (the serving gate trusts the marker, rebuilt roots never
    /// touch it); a marker mismatch reports without stopping.</summary>
    private bool CompareStateRoot(ulong block, StateTree state, List<HistoryWalkMismatch> mismatches, ref ulong compared, bool count = true)
    {
        ValueHash256 rebuilt = new(state.RootHash.Bytes);
        ValueHash256? expected = _headers.TryGetStateRoot(block);
        if (expected is null)
        {
            mismatches.Add(new HistoryWalkMismatch(block, HistoryWalkMismatchKind.MissingHeader, rebuilt, default));
            return false;
        }

        if (count) compared++;

        Span<byte> markerKey = stackalloc byte[BlockBytes];
        BinaryPrimitives.WriteUInt64BigEndian(markerKey, block);
        byte[]? marker = _availableBlocks.Get(markerKey);
        if (marker is not { Length: Hash256.Size } || new ValueHash256(marker) != expected.Value)
        {
            mismatches.Add(new HistoryWalkMismatch(
                block, HistoryWalkMismatchKind.CapturedMarker, marker is { Length: Hash256.Size } ? new ValueHash256(marker) : default, expected.Value));
        }

        if (rebuilt == expected.Value) return true;

        mismatches.Add(new HistoryWalkMismatch(block, HistoryWalkMismatchKind.StateRoot, rebuilt, expected.Value));
        return false;
    }

    private void PartitionAccounts((ulong From, ulong To)[] bounds, SegmentData[] data, RowBudget budget, CancellationToken token)
    {
        ulong to = bounds[^1].To;
        using ISortedView view = _accountHistory.GetViewBetween(ReadOnlySpan<byte>.Empty, MaxBound(AccountRowKeyLength), ReadFlags.HintCacheMiss);
        ValueHash256 currentPath = default;
        bool haveGroup = false;
        int pendingStart = -1;
        while (view.MoveNext())
        {
            token.ThrowIfCancellationRequested();
            ReadOnlySpan<byte> key = view.CurrentKey;
            if (key.Length != AccountRowKeyLength) continue;

            ReadOnlySpan<byte> pathBytes = key[..HistoryKeyLayout.AccountKeyLength];
            if (!haveGroup || !pathBytes.SequenceEqual(currentPath.Bytes))
            {
                currentPath = new ValueHash256(pathBytes);
                haveGroup = true;
                pendingStart = bounds.Length - 1;
            }

            ulong block = _rowFormat.DecodeSuffixBlock(key[HistoryKeyLayout.AccountKeyLength..]);
            if (block > to) continue;

            if (block > bounds[0].From)
            {
                budget.Charge();
                GetOrAdd(data[SegmentOf(bounds, block)].AccountDeltas, block).Add((currentPath, DecodeAccount(view.CurrentValue)));
            }

            // v2 iterates a key's versions newest-first, so this row is the key's value at the start of every
            // segment whose start it is the first row at or below - the read path's floor-seek rule, in bulk.
            if (pendingStart >= 0 && block <= bounds[pendingStart].From)
            {
                Account? account = DecodeAccount(view.CurrentValue);
                while (pendingStart >= 0 && block <= bounds[pendingStart].From)
                {
                    if (account is not null)
                    {
                        budget.Charge();
                        data[pendingStart].StartAccounts.Add((currentPath, account));
                    }

                    pendingStart--;
                }
            }
        }
    }

    // Clears at or below the range's start still matter - they shape each segment's start state - but anything
    // above its end can never be applied, so it is dropped here rather than carried through every segment.
    private Dictionary<byte[], List<ulong>> ScanClears(ulong toInclusive, RowBudget budget, CancellationToken token)
    {
        Dictionary<byte[], List<ulong>> clears = new(Bytes.EqualityComparer);
        using ISortedView view = _storageClears.GetViewBetween(ReadOnlySpan<byte>.Empty, MaxBound(ClearRowKeyLength), ReadFlags.HintCacheMiss);
        while (view.MoveNext())
        {
            token.ThrowIfCancellationRequested();
            ReadOnlySpan<byte> key = view.CurrentKey;
            if (key.Length != ClearRowKeyLength) continue;

            ulong block = BinaryPrimitives.ReadUInt64BigEndian(key[HistoryKeyLayout.AccountKeyLength..]);
            if (block > toInclusive) continue;

            budget.Charge();
            byte[] identity = key[..IdentityLength].ToArray();
            if (!clears.TryGetValue(identity, out List<ulong>? blocks))
            {
                blocks = [];
                clears[identity] = blocks;
            }

            blocks.Add(block);
        }

        return clears;
    }

    private void PartitionStorage(
        (ulong From, ulong To)[] bounds,
        SegmentData[] data,
        Dictionary<byte[], List<ulong>> clearsByIdentity,
        RowBudget budget,
        CancellationToken token)
    {
        ulong to = bounds[^1].To;
        using ISortedView view = _storageHistory.GetViewBetween(ReadOnlySpan<byte>.Empty, MaxBound(StorageRowKeyLength), ReadFlags.HintCacheMiss);
        byte[]? currentFlatKey = null;
        byte[]? currentIdentity = null;
        ValueHash256 currentSlot = default;
        int pendingStart = -1;
        while (view.MoveNext())
        {
            token.ThrowIfCancellationRequested();
            ReadOnlySpan<byte> key = view.CurrentKey;
            if (key.Length != StorageRowKeyLength) continue;

            ReadOnlySpan<byte> flatKey = key[..BaseFlatPersistence.StorageKeyLength];
            if (currentFlatKey is null || !flatKey.SequenceEqual(currentFlatKey))
            {
                currentFlatKey = flatKey.ToArray();
                currentIdentity = new byte[IdentityLength];
                flatKey[..SlotPathOffset].CopyTo(currentIdentity);
                flatKey[SlotSuffixOffset..].CopyTo(currentIdentity.AsSpan(SlotPathOffset));
                currentSlot = new ValueHash256(flatKey[SlotPathOffset..SlotSuffixOffset]);
                pendingStart = bounds.Length - 1;
            }

            ulong block = _rowFormat.DecodeSuffixBlock(key[BaseFlatPersistence.StorageKeyLength..]);
            if (block > to) continue;

            if (block > bounds[0].From)
            {
                budget.Charge();
                GetOrAdd(data[SegmentOf(bounds, block)].SlotDeltas, block).Add((currentIdentity!, currentSlot, view.CurrentValue.ToArray()));
            }

            if (pendingStart >= 0 && block <= bounds[pendingStart].From)
            {
                byte[]? value = view.CurrentValue.IsEmpty ? null : view.CurrentValue.ToArray();
                while (pendingStart >= 0 && block <= bounds[pendingStart].From)
                {
                    int segment = pendingStart;
                    pendingStart--;
                    if (value is null) continue;
                    if (KilledByClear(clearsByIdentity, currentIdentity!, writtenAt: block, asOf: bounds[segment].From)) continue;

                    budget.Charge();
                    if (!data[segment].StartSlots.TryGetValue(currentIdentity!, out List<(ValueHash256, byte[])>? slots))
                    {
                        slots = [];
                        data[segment].StartSlots[currentIdentity!] = slots;
                    }

                    slots.Add((currentSlot, value));
                }
            }
        }
    }

    // bounds are contiguous, so the delta segment for a block in (bounds[0].From, bounds[^1].To] is the unique
    // one with From < block <= To.
    private static int SegmentOf((ulong From, ulong To)[] bounds, ulong block)
    {
        int lo = 0;
        int hi = bounds.Length - 1;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (block <= bounds[mid].To) hi = mid;
            else lo = mid + 1;
        }

        return lo;
    }

    /// <summary>The v2 destruct rule: a slot value written at <paramref name="writtenAt"/> is dead as of
    /// <paramref name="asOf"/> when a clear landed in <c>(writtenAt, asOf]</c> - a write in the destruct's own
    /// block is the post-destruct (resurrected) value and survives.</summary>
    private static bool KilledByClear(Dictionary<byte[], List<ulong>> clearsByIdentity, byte[] identity, ulong writtenAt, ulong asOf)
    {
        if (!clearsByIdentity.TryGetValue(identity, out List<ulong>? blocks)) return false;
        foreach (ulong clearBlock in blocks)
        {
            if (clearBlock > writtenAt && clearBlock <= asOf) return true;
        }

        return false;
    }

    private StorageTree GetOrMaterialize(
        Dictionary<byte[], StorageTree> tries,
        Dictionary<byte[], List<(ValueHash256 SlotPath, byte[] Value)>> startSlots,
        byte[] identity)
    {
        if (tries.TryGetValue(identity, out StorageTree? tree)) return tree;

        tree = new StorageTree(new RawScopedTrieStore(new MemDb()), _logManager);
        if (startSlots.TryGetValue(identity, out List<(ValueHash256 SlotPath, byte[] Value)>? slots))
        {
            foreach ((ValueHash256 slotPath, byte[] value) in slots)
            {
                tree.Set(slotPath, value, rlpEncode: !_rlpWrapSlots);
            }
        }

        tries[identity] = tree;
        return tree;
    }

    private static Account? DecodeAccount(ReadOnlySpan<byte> value)
    {
        if (value.IsEmpty) return null;

        RlpReader context = new(value);
        if (!AccountDecoder.Slim.TryDecodeStruct(ref context, out AccountStruct account))
        {
            throw new InvalidOperationException("An account history row failed to decode; the column is corrupt.");
        }

        return new Account(account.Nonce, account.Balance, account.StorageRoot.ToCommitment(), account.CodeHash.ToCommitment());
    }

    private static List<TItem> GetOrAdd<TItem>(SortedDictionary<ulong, List<TItem>> map, ulong block)
    {
        if (!map.TryGetValue(block, out List<TItem>? list))
        {
            list = [];
            map[block] = list;
        }

        return list;
    }

    private static byte[] MaxBound(int rowKeyLength)
    {
        byte[] bound = new byte[rowKeyLength + 1];
        bound.AsSpan().Fill(0xFF);
        return bound;
    }
}
