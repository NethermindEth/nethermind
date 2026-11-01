// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.State.Flat.Persistence;
using Nethermind.Trie;

namespace Nethermind.State.Flat.History.Prestate;

/// <summary>Answers account and slot reads of one historical state from the pre-state record of that state, and
/// every key the record does not hold from flat history, as before.</summary>
/// <remarks>
/// A record value is the exact value at this state (the record is keyed and checked by the state's number and root),
/// so a read answers the same either way. A record that is not cached yet is opened only after
/// <see cref="PrestateRecordCache.LazyOpenReads"/> history reads, so a single point read never pays for it. A slot
/// of an account with a poisoned self-destruct above this state goes to history, which fails closed there.
/// </remarks>
internal sealed class RecordBackedPersistenceReader : IPersistence.IPersistenceReader
{
    private readonly HistoryBackedPersistenceReader _history;
    private readonly PrestateRecordCache _cache;
    private readonly StateId _block;
    private PrestateRecord? _record;
    private int _openAttempted;
    private long _historyReads;
    private long _recordReads;

    public RecordBackedPersistenceReader(HistoryBackedPersistenceReader history, PrestateRecordCache cache, in StateId block)
    {
        _history = history;
        _cache = cache;
        _block = block;
        if (cache.TryLeaseCached(block, out PrestateRecord? cached))
        {
            _record = cached;
            _openAttempted = 1;
        }
        else if (cache.LazyOpenReads == 0)
        {
            Open();
        }
    }

    public StateId CurrentState => _block;

    public Account? GetAccount(Address address)
    {
        PrestateRecord? record = Volatile.Read(ref _record);
        if (record is not null && _cache.IsEnabled && record.TryGetAccount(address, out Account? account))
        {
            Interlocked.Increment(ref _recordReads);
            return ShouldVerify() ? VerifyAccount(address, account) : account;
        }

        OnHistoryRead(record);
        return _history.GetAccount(address);
    }

    public bool TryGetSlot(Address address, in UInt256 slot, ref UInt256 outValue)
    {
        PrestateRecord? record = Volatile.Read(ref _record);
        if (record is not null && _cache.IsEnabled && record.TryGetSlot(address, slot, out UInt256 value) && !_history.HasPoisonedClearAbove(address))
        {
            Interlocked.Increment(ref _recordReads);
            if (ShouldVerify()) VerifySlot(address, slot, ref value);
            if (value.IsZero) return false;
            outValue = value;
            return true;
        }

        OnHistoryRead(record);
        return _history.TryGetSlot(address, slot, ref outValue);
    }

    private void OnHistoryRead(PrestateRecord? record)
    {
        long reads = Interlocked.Increment(ref _historyReads);
        if (record is null && reads >= _cache.LazyOpenReads && Volatile.Read(ref _openAttempted) == 0) Open();
    }

    private void Open()
    {
        if (Interlocked.Exchange(ref _openAttempted, 1) != 0) return;
        if (_cache.TryLease(_block, out PrestateRecord? record)) Volatile.Write(ref _record, record);
    }

    private bool ShouldVerify()
    {
        int percent = _cache.VerifyPercent;
        return percent > 0 && (percent >= 100 || Random.Shared.Next(100) < percent);
    }

    private Account? VerifyAccount(Address address, Account? fromRecord)
    {
        Account? fromHistory = _history.GetAccount(address);
        if (!SameAccount(fromHistory, fromRecord)) _cache.Disable($"account {address} at state {_block}");
        return fromHistory;
    }

    private void VerifySlot(Address address, in UInt256 slot, ref UInt256 value)
    {
        UInt256 fromHistory = default;
        _history.TryGetSlot(address, slot, ref fromHistory);
        if (fromHistory != value) _cache.Disable($"slot {slot} of {address} at state {_block}");
        value = fromHistory;
    }

    private static bool SameAccount(Account? left, Account? right) =>
        left is null ? right is null
        : right is not null && left.Nonce == right.Nonce && left.Balance == right.Balance && left.CodeHash == right.CodeHash && left.StorageRoot == right.StorageRoot;

    public void Dispose()
    {
        Metrics.AddPrestateRecordKeyReads(_recordReads, _record is null ? 0 : _historyReads);
        _record?.Dispose();
        _history.Dispose();
    }

    public bool IsPreimageMode => _history.IsPreimageMode;

    public byte[]? TryLoadStateRlp(in TreePath path, ReadFlags flags) => _history.TryLoadStateRlp(path, flags);

    public byte[]? TryLoadStorageRlp(Hash256 address, in TreePath path, ReadFlags flags) => _history.TryLoadStorageRlp(address, path, flags);

    public bool TryLoadStateRlp<TArg>(in TreePath path, ReadFlags flags, ReadOnlySpanAction<byte, TArg> action, TArg arg) where TArg : allows ref struct =>
        _history.TryLoadStateRlp(path, flags, action, arg);

    public bool TryLoadStorageRlp<TArg>(Hash256 address, in TreePath path, ReadFlags flags, ReadOnlySpanAction<byte, TArg> action, TArg arg) where TArg : allows ref struct =>
        _history.TryLoadStorageRlp(address, path, flags, action, arg);

    public byte[]? GetAccountRaw(in ValueHash256 addrHash) => _history.GetAccountRaw(addrHash);

    public bool TryGetStorageRaw(in ValueHash256 addrHash, in ValueHash256 slotHash, ref UInt256 value) => _history.TryGetStorageRaw(addrHash, slotHash, ref value);

    public IPersistence.IFlatIterator CreateAccountIterator(in ValueHash256 startKey, in ValueHash256 endKey) => _history.CreateAccountIterator(startKey, endKey);

    public IPersistence.IFlatIterator CreateStorageIterator(in ValueHash256 accountKey, in ValueHash256 startSlotKey, in ValueHash256 endSlotKey) =>
        _history.CreateStorageIterator(accountKey, startSlotKey, endSlotKey);
}
