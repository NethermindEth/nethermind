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
/// <remarks>
/// Two row formats, chosen once from the stamped format version (never mixed on the same DB — see
/// <see cref="HistoryAvailability.ResolveFormatVersion"/>): v2 (<see cref="HistoryStore"/>, post-value, descending
/// suffix) does a single floor-seek and never needs a fallback. v3 (<see cref="HistoryStoreV3"/>, pre-value,
/// ascending suffix) forward-seeks for the first change after B, and if none is captured, falls through to the
/// persisted (never tip/snapshot-stacked) live flat column — see <see cref="HistoryStoreV3"/>'s remarks for why
/// that fallback is sound.
/// </remarks>
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

    public HistoryReader(IColumnsDb<FlatDbColumns> db, IColumnsDb<FlatHistoryColumns> history, IFlatDbConfig config, HistoryAvailability availability, HistoryRowFormat rowFormat, ILogManager logManager)
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

        // rowFormat is resolved from config, not just the on-disk stamp (see HistoryRowFormat's remarks): a brand
        // new windowed DB has no stamp at all until its writer's first capture, and a reader constructed before
        // that (the normal DI startup order) must still resolve to v3, or it would speak v2 to a writer that is
        // about to speak v3 the moment it captures anything.
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

    /// <summary>
    /// Whether <paramref name="state"/> can be served from history: it is at or below the contiguous watermark, at or
    /// above the retention floor, and its state root matches the captured root at that height, so a non-canonical
    /// block hash is rejected (EIP-1898).
    /// </summary>
    public bool IsAvailable(in StateId state) => _availability.Matches(state.BlockNumber, state.StateRoot);

    /// <summary>Whether <paramref name="block"/> is covered by the watermark but has been pruned below the
    /// retention floor — distinct from "never captured" so a caller can fail loudly instead of reporting absence.</summary>
    public bool IsPrunedBelowFloor(ulong block) => _availability.IsCovered(block) && _availability.IsBelowGlobalFloor(block);

    /// <summary>Whether <paramref name="state"/> is covered and its root matches — deliberately independent of the
    /// general floor, so a caller that already knows a block sits below it (a restricted, per-slice bundle) can
    /// still re-verify canonicity before serving anything from it.</summary>
    public bool IsCoveredAndRootMatches(in StateId state) => _availability.IsCoveredAndRootMatches(state.BlockNumber, state.StateRoot);

    /// <summary>Whether <paramref name="block"/> sits below the published general (all-keys) retention floor.</summary>
    public bool IsBelowGlobalFloor(ulong block) => _availability.IsBelowGlobalFloor(block);

    /// <summary>Every configured per-address slice scope, for a restricted bundle to carry as its own in-memory,
    /// no-further-DB-reads gate.</summary>
    public IReadOnlyList<ScopeFloor> GetSliceScopes() => _availability.GetScopes();

    /// <summary>
    /// Resolves the account as of <paramref name="block"/>. Returns <c>false</c> when the account did not exist at
    /// that block — either it never changed at/before it, or its latest change at/before it was a deletion.
    /// </summary>
    [SkipLocalsInit]
    public bool TryGetAccount(ulong block, Address address, out AccountStruct account)
    {
        ReadOnlySpan<byte> flatKey = HistoryKeyLayout.EncodeAccountKey(
            stackalloc byte[HistoryKeyLayout.AccountKeyLength], address.ToAccountPath);

        Span<byte> valueBuffer = stackalloc byte[AccountValueBufferSize];
        int written = _isV3
            ? TryGetAccountV3(block, flatKey, valueBuffer)
            : _accountHistory!.TryGetAt(block, flatKey, valueBuffer);
        if (written <= 0) // -1 (or persisted-column miss) = absent, 0 = deletion tombstone
        {
            account = default;
            return false;
        }

        RlpReader context = new(valueBuffer[..written]);
        return AccountDecoder.Slim.TryDecodeStruct(ref context, out account);
    }

    private int TryGetAccountV3(ulong block, ReadOnlySpan<byte> flatKey, Span<byte> valueBuffer)
    {
        int written = _accountHistoryV3!.TryGetValueBeforeNextChange(block, flatKey, valueBuffer, out _);
        return written >= 0 ? written : _persistedAccounts!.Get(HistoryKeyLayout.ToFlatStateKey(flatKey), valueBuffer);
    }

    /// <summary>
    /// Resolves the storage slot as of <paramref name="block"/>. Returns <c>false</c> when the slot was unset at
    /// that block — either it never changed at/before it, or its latest change at/before it cleared it.
    /// </summary>
    public bool TryGetStorage(ulong block, Address address, in UInt256 index, out SlotValue value) =>
        TryGetStorage(block, address, index, out value, clearsCache: null);

    /// <param name="clearsCache">Optional per-scope memo that skips the per-slot self-destruct probe for the
    /// overwhelmingly common account with no clear markers at all. Consulted only on the v2 path — see the v3
    /// gap note below.</param>
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
            // A destruct whose persisted-slot count exceeded HistoryWriter's enumeration cap left no per-slot
            // pre-value rows for this account above the destruct block — silently falling through would omit
            // slots rather than answer wrong, but fail closed instead: a caller cannot tell "no history" from
            // "history exists but was too large to record" otherwise.
            ReadOnlySpan<byte> destructAccountKey = HistoryKeyLayout.EncodeAccountKey(
                stackalloc byte[HistoryKeyLayout.AccountKeyLength], addrHash);
            if (_storageClears.HasPoisonedClearAbove(destructAccountKey, block))
                throw new StateUnavailableException(
                    $"Storage history for account {addrHash} above block {block} was not fully captured (a self-destruct " +
                    "exceeded the per-slot enumeration cap) - the exact value cannot be determined.");

            int written = _storageHistoryV3!.TryGetValueBeforeNextChange(block, flatKey, valueBuffer, out _);
            if (written < 0) written = _persistedStorage!.Get(flatKey, valueBuffer);
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

        // A self-destruct between the slot's last write and the read block kills the value. The live column
        // expresses the destruct as a range-delete, which leaves no per-slot tombstone in the history.
        ReadOnlySpan<byte> accountKey = HistoryKeyLayout.EncodeAccountKey(
            stackalloc byte[HistoryKeyLayout.AccountKeyLength], addrHash);
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
