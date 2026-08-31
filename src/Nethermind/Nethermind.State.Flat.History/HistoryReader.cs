// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Flat.Persistence;

namespace Nethermind.State.Flat.History;

/// <summary>
/// Reads finalized historical state "as of block B" from the history columns. Serves block-parameter reads below
/// the finalization barrier, where the per-block snapshots have already been pruned.
/// </summary>
/// <remarks>v2 floor-seeks and needs no fallback; v3 forward-seeks and falls through to the persisted flat
/// column - see <see cref="HistoryStoreV3"/>.</remarks>
public sealed class HistoryReader
{
    // Slim-format account RLP is at most nonce + balance + two 32-byte hashes; 256 bytes is ample headroom.
    private const int AccountValueBufferSize = 256;

    private readonly HistoryStore? _accountHistory;
    private readonly HistoryStore? _storageHistory;
    private readonly HistoryStoreV3? _accountHistoryV3;
    private readonly HistoryStoreV3? _storageHistoryV3;
    private readonly IDb? _persistedAccounts;
    private readonly IDb? _persistedStorage;
    private readonly StorageClearStore _storageClears;
    private readonly HistoryAvailability _availability;
    private readonly bool _rlpWrapSlots;
    private readonly bool _isV3;

    public HistoryReader(IColumnsDb<FlatDbColumns> db, IColumnsDb<FlatHistoryColumns> history, HistoryAvailability availability, HistoryRowFormat rowFormat, ILogManager logManager)
    {
        ArgumentNullException.ThrowIfNull(history);
        ILogger logger = logManager.GetClassLogger<HistoryReader>();
        _rlpWrapSlots = BasePersistence.ResolveSlotEncoding(
            db,
            (ISortedKeyValueStore)db.GetColumnDb(FlatDbColumns.Storage),
            logger);
        _storageClears = new StorageClearStore(history.GetColumnDb(FlatHistoryColumns.StorageClears));
        _availability = availability;
        _availability.VerifyFormat();

        // From config, not the on-disk stamp: a new windowed DB has no stamp until its writer's first capture.
        _isV3 = rowFormat.IsV3;
        if (_isV3)
        {
            _accountHistoryV3 = new HistoryStoreV3(history.GetColumnDb(FlatHistoryColumns.AccountHistory));
            _storageHistoryV3 = new HistoryStoreV3(history.GetColumnDb(FlatHistoryColumns.StorageHistory));
            _persistedAccounts = db.GetColumnDb(FlatDbColumns.Account);
            _persistedStorage = db.GetColumnDb(FlatDbColumns.Storage);
        }
        else
        {
            _accountHistory = new HistoryStore(history.GetColumnDb(FlatHistoryColumns.AccountHistory), logger);
            _storageHistory = new HistoryStore(history.GetColumnDb(FlatHistoryColumns.StorageHistory), logger);
        }
    }

    /// <summary>Whether contiguous history has been captured up to and including <paramref name="block"/>.</summary>
    public bool HasHistoryForBlock(ulong block) => _availability.IsCovered(block);

    /// <summary>At or below the watermark, at or above the floor, and root-matching, so EIP-1898 rejects a
    /// non-canonical hash.</summary>
    public bool IsAvailable(in StateId state) => _availability.Matches(state.BlockNumber, state.StateRoot);

    /// <summary>Covered by the watermark but pruned below the floor - distinct from never captured.</summary>
    public bool IsPrunedBelowFloor(ulong block) => _availability.IsCovered(block) && _availability.IsBelowGlobalFloor(block);

    /// <summary>Covered and root-matching, independent of the general floor, for a per-slice bundle to
    /// re-verify canonicity.</summary>
    public bool IsCoveredAndRootMatches(in StateId state) => _availability.IsCoveredAndRootMatches(state.BlockNumber, state.StateRoot);

    /// <summary>Whether <paramref name="block"/> sits below the published general (all-keys) retention floor.</summary>
    public bool IsBelowGlobalFloor(ulong block) => _availability.IsBelowGlobalFloor(block);

    /// <summary>Every configured slice scope, for a restricted bundle's in-memory gate. Internal because the
    /// array and its keys are shared and mutable.</summary>
    internal ScopeFloor[] GetSliceScopesArray() => _availability.GetScopesArray();

    /// <summary>Resolves the account as of <paramref name="block"/>. False when it did not exist there.</summary>
    [SkipLocalsInit]
    public bool TryGetAccount(ulong block, Address address, out AccountStruct account)
    {
        ValueHash256 accountPath = address.ToAccountPath;
        ReadOnlySpan<byte> flatKey = accountPath.Bytes;

        Span<byte> valueBuffer = stackalloc byte[AccountValueBufferSize];
        int written = _isV3
            ? TryGetAccountV3(block, flatKey, valueBuffer)
            : _accountHistory!.TryGetAt(block, flatKey, valueBuffer);
        if (written <= 0) // -1 = no such row; 0 = deletion tombstone, or a live-column miss under v3
        {
            account = default;
            return false;
        }

        RlpReader context = new(valueBuffer[..written]);
        return AccountDecoder.Slim.TryDecodeStruct(ref context, out account);
    }

    /// <remarks>A found row is always the answer. A miss falls through to the live column, and a capture round can
    /// commit in between; the second seek settles that, since a row commits before the persist superseding it. It is
    /// worth only when a capture actually landed, so the generation sampled before the first seek gates it.</remarks>
    [SkipLocalsInit]
    private int TryGetAccountV3(ulong block, ReadOnlySpan<byte> flatKey, Span<byte> valueBuffer)
    {
        long generation = _availability.CaptureGeneration;

        int written = _accountHistoryV3!.TryGetValueBeforeNextChange(block, flatKey, valueBuffer, out _);
        if (written >= 0) return written;

        Span<byte> liveBuffer = stackalloc byte[AccountValueBufferSize];
        int live = _persistedAccounts!.Get(HistoryKeyLayout.ToFlatStateKey(flatKey), liveBuffer);

        if (_availability.HasCapturedSince(generation))
        {
            written = _accountHistoryV3.TryGetValueBeforeNextChange(block, flatKey, valueBuffer, out _);
            if (written >= 0) return written;
        }

        if (live > 0) liveBuffer[..live].CopyTo(valueBuffer);
        return live;
    }

    /// <summary>Resolves the slot as of <paramref name="block"/>. False when it was unset there.</summary>
    public bool TryGetStorage(ulong block, Address address, in UInt256 index, out SlotValue value) =>
        TryGetStorage(block, address, index, out value, clearsCache: null);

    /// <param name="clearsCache">Skips the per-slot destruct probes for an account with no clear markers.</param>
    [SkipLocalsInit]
    internal bool TryGetStorage(ulong block, Address address, in UInt256 index, out SlotValue value, StorageClearsScopeCache? clearsCache)
    {
        ValueHash256 addrHash = address.ToAccountPath;
        ValueHash256 slotHash = ValueKeccak.Zero;
        StorageTree.ComputeKeyWithLookup(index, ref slotHash);
        ReadOnlySpan<byte> flatKey = BaseFlatPersistence.EncodeStorageKeyHashedWithShortPrefix(
            stackalloc byte[BaseFlatPersistence.StorageKeyLength], addrHash, slotHash);

        Span<byte> valueBuffer = stackalloc byte[BaseFlatPersistence.RlpSlotValueBufferSize];

        if (_isV3)
        {
            // Seek, live only on a miss, then seek again only if a capture landed - see TryGetAccountV3's remarks.
            long generation = _availability.CaptureGeneration;
            int written = _storageHistoryV3!.TryGetValueBeforeNextChange(block, flatKey, valueBuffer, out ulong rowBlock);
            if (written < 0)
            {
                Span<byte> liveBuffer = stackalloc byte[BaseFlatPersistence.RlpSlotValueBufferSize];
                int live = _persistedStorage!.Get(flatKey, liveBuffer);

                if (_availability.HasCapturedSince(generation))
                {
                    written = _storageHistoryV3.TryGetValueBeforeNextChange(block, flatKey, valueBuffer, out rowBlock);
                }

                if (written < 0)
                {
                    rowBlock = ulong.MaxValue;
                    if (live > 0) liveBuffer[..live].CopyTo(valueBuffer);
                    written = live;
                }
            }

            // An over-cap destruct left no per-slot rows, so fail closed rather than omit slots.
            bool poisoned = clearsCache is not null
                ? clearsCache.TryGetPoisonedClearAbove(addrHash, _storageClears, block, out ulong poisonBlock)
                : _storageClears.TryGetPoisonedClearAbove(addrHash.Bytes, block, out poisonBlock);
            if (poisoned && rowBlock > poisonBlock)
                throw new StateUnavailableException(
                    $"Storage history for account {addrHash} above block {block} was not fully captured (a self-destruct " +
                    "exceeded the per-slot enumeration cap) - the exact value cannot be determined.");

            if (written <= 0) { value = default; return false; }
            value = DecodeSlotValue(valueBuffer[..written]);
            return true;
        }

        int v2Written = _storageHistory!.TryGetAt(block, flatKey, valueBuffer, out ulong changedAtBlock);
        if (v2Written <= 0) // -1 = never changed at/before block, 0 = cleared tombstone
        {
            value = default;
            return false;
        }

        // A destruct between the last write and the read block kills the value, leaving no per-slot tombstone.
        ReadOnlySpan<byte> accountKey = addrHash.Bytes;
        bool mayHaveClear = clearsCache?.HasAnyClearUpTo(addrHash, accountKey, _storageClears, block) ?? true;
        if (mayHaveClear && _storageClears.HasClearInRange(accountKey, changedAtBlock, block))
        {
            value = default;
            return false;
        }

        value = DecodeSlotValue(valueBuffer[..v2Written]);
        return true;
    }

    private SlotValue DecodeSlotValue(ReadOnlySpan<byte> stored)
    {
        if (_rlpWrapSlots)
        {
            RlpReader context = new(stored);
            stored = context.DecodeByteArraySpan();
        }

        return SlotValue.FromSpanWithoutLeadingZero(stored);
    }
}
