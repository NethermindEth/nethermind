// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
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

public enum HistoryWalkMismatchKind : byte
{
    StateRoot,
    StorageRoot,
    MissingHeader,
    MissingAccountRow,
    CapturedMarker,
}

public readonly record struct HistoryWalkMismatch(ulong Block, HistoryWalkMismatchKind Kind, ValueHash256 Rebuilt, ValueHash256 Expected);

public readonly record struct HistoryWalkVerdict(bool Verified, ulong BlocksCompared, IReadOnlyList<HistoryWalkMismatch> Mismatches);

/// <summary>
/// Proves an unwindowed (v2) archive's history content against this node's own headers at EVERY block of a range:
/// builds the state at the range's start from the rows alone, anchors it to the header, then walks forward
/// applying each block's recorded post-values and comparing the recomputed root to that block's header. No
/// execution is involved because the headers already are consensus-verified execution output - the only question
/// left is whether the rows this node holds produce those commitments. Per-block (not sampled, not tip-only) on
/// purpose: a row attributing a change to the wrong block leaves the tip root correct while every as-of answer in
/// between is wrong, and only the root check at the misattributed height catches it.
/// </summary>
/// <remarks>
/// Trust anchors, all local: the header root at the range start (anchors the built start state), the header root
/// at every walked block (anchors the account rows), and each account record's own storageRoot - itself anchored
/// by the state root - against which the account's storage trie, rebuilt from slot rows, is compared whenever its
/// slots change. Ranges are independently anchored at both ends, so segments of the chain can be verified
/// concurrently by separate instances.
///
/// v2 only: its rows are post-values ("at block b this key became V"), exactly the instruction a forward walk
/// applies. A windowed (v3) database is refused - its rows are pre-values, unchanged keys carry no rows at all,
/// and the retention floor removed the ancestry a genesis-anchored walk needs.
///
/// This pass holds the range's start state and deltas in memory - segment sizing against available memory is the
/// caller's job (see the 39-10 task file for the spill-store hardening this deliberately defers).
/// </remarks>
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
    private readonly ICloneHeaderSource _headers;
    private readonly HistoryRowFormat _rowFormat;
    private readonly bool _rlpWrapSlots;
    private readonly ILogManager _logManager;
    private readonly ILogger _logger;

    /// <summary>Resolves the slot encoding from the live flat database exactly the way <see cref="HistoryReader"/>
    /// does, so the verifier decodes slot rows with the same convention the writer stored them under.</summary>
    public HistoryWalkVerifier(
        IColumnsDb<FlatDbColumns> db,
        IColumnsDb<FlatHistoryColumns> history,
        ICloneHeaderSource headers,
        HistoryRowFormat rowFormat,
        ILogManager logManager)
        : this(
            history,
            headers,
            rowFormat,
            BasePersistence.ResolveSlotEncoding(db, (ISortedKeyValueStore)db.GetColumnDb(FlatDbColumns.Storage), logManager.GetClassLogger<HistoryWalkVerifier>()),
            logManager)
    {
    }

    public HistoryWalkVerifier(
        IColumnsDb<FlatHistoryColumns> history,
        ICloneHeaderSource headers,
        HistoryRowFormat rowFormat,
        bool rlpWrapSlots,
        ILogManager logManager)
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
    }

    /// <summary>
    /// Splits <c>[fromInclusive, toInclusive]</c> into <paramref name="segments"/> contiguous ranges and verifies
    /// them concurrently - sound because every segment is independently anchored: its start state is compared to
    /// its own start header before its walk begins, so no segment consumes another's output. Adjacent segments
    /// share their boundary block's comparison; the double-check is harmless and keeps the ranges self-contained.
    /// All scans and tries are per-call state, so the concurrent calls share nothing but the read-only columns.
    /// </summary>
    public HistoryWalkVerdict VerifyRangeParallel(ulong fromInclusive, ulong toInclusive, int segments, CancellationToken token)
    {
        if (segments < 1) throw new ArgumentOutOfRangeException(nameof(segments));

        ulong span = toInclusive - fromInclusive;
        int effectiveSegments = (int)Math.Min((ulong)segments, Math.Max(span, 1));
        if (effectiveSegments == 1) return VerifyRange(fromInclusive, toInclusive, token);

        (ulong From, ulong To)[] bounds = new (ulong, ulong)[effectiveSegments];
        for (int i = 0; i < effectiveSegments; i++)
        {
            bounds[i] = (
                fromInclusive + span * (ulong)i / (ulong)effectiveSegments,
                fromInclusive + span * ((ulong)i + 1) / (ulong)effectiveSegments);
        }

        HistoryWalkVerdict[] verdicts = new HistoryWalkVerdict[effectiveSegments];
        Parallel.For(0, effectiveSegments, i => verdicts[i] = VerifyRange(bounds[i].From, bounds[i].To, token));

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

    public HistoryWalkVerdict VerifyRange(ulong fromInclusive, ulong toInclusive, CancellationToken token)
    {
        if (fromInclusive > toInclusive)
            throw new ArgumentException($"Range start {fromInclusive} is above its end {toInclusive}.", nameof(fromInclusive));

        List<HistoryWalkMismatch> mismatches = [];

        (List<(ValueHash256 Path, Account Account)> startAccounts,
            SortedDictionary<ulong, List<(ValueHash256 Path, Account? Account)>> accountDeltas) = ScanAccounts(fromInclusive, toInclusive, token);

        Dictionary<byte[], List<ulong>> clearsByIdentity = ScanClears(token);

        (Dictionary<byte[], List<(ValueHash256 SlotPath, byte[] Value)>> startSlots,
            SortedDictionary<ulong, List<(byte[] Identity, ValueHash256 SlotPath, byte[] Value)>> slotDeltas) =
            ScanStorage(fromInclusive, toInclusive, clearsByIdentity, token);

        SortedDictionary<ulong, List<byte[]>> clearsByBlock = [];
        foreach ((byte[] identity, List<ulong> blocks) in clearsByIdentity)
        {
            foreach (ulong block in blocks)
            {
                if (block > fromInclusive && block <= toInclusive) GetOrAdd(clearsByBlock, block).Add(identity);
            }
        }

        StateTree state = new(new RawScopedTrieStore(new MemDb()), _logManager);
        foreach ((ValueHash256 path, Account account) in startAccounts)
        {
            state.Set(path, account);
        }

        state.UpdateRootHash();

        ulong compared = 0;
        if (!CompareStateRoot(fromInclusive, state, mismatches, ref compared))
        {
            return new HistoryWalkVerdict(false, compared, mismatches);
        }

        Dictionary<byte[], StorageTree> storageTries = new(Bytes.EqualityComparer);

        for (ulong block = fromInclusive + 1; block <= toInclusive; block++)
        {
            token.ThrowIfCancellationRequested();

            if (clearsByBlock.TryGetValue(block, out List<byte[]>? clearedIdentities))
            {
                foreach (byte[] identity in clearedIdentities)
                {
                    storageTries[identity] = new StorageTree(new RawScopedTrieStore(new MemDb()), _logManager);
                }
            }

            HashSet<byte[]>? touched = null;
            if (slotDeltas.TryGetValue(block, out List<(byte[] Identity, ValueHash256 SlotPath, byte[] Value)>? slots))
            {
                touched = new HashSet<byte[]>(Bytes.EqualityComparer);
                foreach ((byte[] identity, ValueHash256 slotPath, byte[] value) in slots)
                {
                    StorageTree tree = GetOrMaterialize(storageTries, startSlots, identity);
                    tree.Set(slotPath, value, rlpEncode: !_rlpWrapSlots);
                    touched.Add(identity);
                }
            }

            Dictionary<byte[], Account?>? accountsAtBlock = null;
            accountDeltas.TryGetValue(block, out List<(ValueHash256 Path, Account? Account)>? accountRows);
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

    /// <summary>Whether the walk can continue - a state-root or missing-header failure at <paramref name="block"/>
    /// leaves every later comparison meaningless, a clean match keeps going. Also checks the block's captured
    /// <c>AvailableBlocks</c> marker against the same header: the serving gate trusts the marker (an EIP-1898 hash
    /// is accepted or refused by comparing against it), so a corrupt marker over honest rows would still misroute
    /// serving - and rebuilt roots never touch it. A marker mismatch reports without stopping; it does not affect
    /// how the state evolves.</summary>
    private bool CompareStateRoot(ulong block, StateTree state, List<HistoryWalkMismatch> mismatches, ref ulong compared)
    {
        ValueHash256 rebuilt = new(state.RootHash.Bytes);
        ValueHash256? expected = _headers.TryGetStateRoot(block);
        if (expected is null)
        {
            mismatches.Add(new HistoryWalkMismatch(block, HistoryWalkMismatchKind.MissingHeader, rebuilt, default));
            return false;
        }

        compared++;

        Span<byte> markerKey = stackalloc byte[BlockBytes];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(markerKey, block);
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

    private (List<(ValueHash256 Path, Account Account)> Start, SortedDictionary<ulong, List<(ValueHash256 Path, Account? Account)>> Deltas)
        ScanAccounts(ulong from, ulong to, CancellationToken token)
    {
        List<(ValueHash256, Account)> start = [];
        SortedDictionary<ulong, List<(ValueHash256, Account?)>> deltas = [];

        using ISortedView view = _accountHistory.GetViewBetween(ReadOnlySpan<byte>.Empty, MaxBound(AccountRowKeyLength));
        ValueHash256 currentPath = default;
        bool haveGroup = false;
        bool startChosen = false;
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
                startChosen = false;
            }

            ulong block = _rowFormat.DecodeSuffixBlock(key[HistoryKeyLayout.AccountKeyLength..]);
            if (block > to) continue;

            if (block > from)
            {
                GetOrAdd(deltas, block).Add((currentPath, DecodeAccount(view.CurrentValue)));
            }
            else if (!startChosen)
            {
                // v2 iterates a key's versions newest-first, so the first row at or below the range start is the
                // key's value there - the same floor-seek rule the v2 read path applies, done in bulk.
                startChosen = true;
                Account? account = DecodeAccount(view.CurrentValue);
                if (account is not null) start.Add((currentPath, account));
            }
        }

        return (start, deltas);
    }

    private Dictionary<byte[], List<ulong>> ScanClears(CancellationToken token)
    {
        Dictionary<byte[], List<ulong>> clears = new(Bytes.EqualityComparer);
        using ISortedView view = _storageClears.GetViewBetween(ReadOnlySpan<byte>.Empty, MaxBound(ClearRowKeyLength));
        while (view.MoveNext())
        {
            token.ThrowIfCancellationRequested();
            ReadOnlySpan<byte> key = view.CurrentKey;
            if (key.Length != ClearRowKeyLength) continue;

            byte[] identity = key[..IdentityLength].ToArray();
            ulong block = System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(key[HistoryKeyLayout.AccountKeyLength..]);
            if (!clears.TryGetValue(identity, out List<ulong>? blocks))
            {
                blocks = [];
                clears[identity] = blocks;
            }

            blocks.Add(block);
        }

        return clears;
    }

    private (Dictionary<byte[], List<(ValueHash256 SlotPath, byte[] Value)>> Start,
        SortedDictionary<ulong, List<(byte[] Identity, ValueHash256 SlotPath, byte[] Value)>> Deltas)
        ScanStorage(ulong from, ulong to, Dictionary<byte[], List<ulong>> clearsByIdentity, CancellationToken token)
    {
        Dictionary<byte[], List<(ValueHash256, byte[])>> start = new(Bytes.EqualityComparer);
        SortedDictionary<ulong, List<(byte[], ValueHash256, byte[])>> deltas = [];

        using ISortedView view = _storageHistory.GetViewBetween(ReadOnlySpan<byte>.Empty, MaxBound(StorageRowKeyLength));
        byte[]? currentFlatKey = null;
        byte[]? currentIdentity = null;
        ValueHash256 currentSlot = default;
        bool startChosen = false;
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
                startChosen = false;
            }

            ulong block = _rowFormat.DecodeSuffixBlock(key[BaseFlatPersistence.StorageKeyLength..]);
            if (block > to) continue;

            if (block > from)
            {
                GetOrAdd(deltas, block).Add((currentIdentity!, currentSlot, view.CurrentValue.ToArray()));
            }
            else if (!startChosen)
            {
                startChosen = true;
                if (view.CurrentValue.IsEmpty) continue;
                if (KilledByClear(clearsByIdentity, currentIdentity!, writtenAt: block, asOf: from)) continue;

                if (!start.TryGetValue(currentIdentity!, out List<(ValueHash256, byte[])>? slots))
                {
                    slots = [];
                    start[currentIdentity!] = slots;
                }

                slots.Add((currentSlot, view.CurrentValue.ToArray()));
            }
        }

        return (start, deltas);
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
