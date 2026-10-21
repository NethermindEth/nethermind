// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;

namespace Nethermind.State.Flat.History.Changesets;

/// <summary>Accumulates one transaction's writes, keeping only the last value written to each account field and
/// each slot, then packs them once.</summary>
internal sealed class ChangesetCollector
{
    private const int MaxRetainedEntries = 512;
    // An account change is a struct of about 112 B kept inline, so this keeps the retained list near 14 KB.
    private const int MaxRetainedAccounts = 128;
    private const int MaxRetainedPacked = 64 * 1024;

    // The changes live in one reusable list, found through their index, so a transaction allocates none and every
    // change is updated in place.
    private readonly Dictionary<AddressAsKey, int> _accountIndexes = [];
    private readonly List<AccountChange> _accounts = [];
    private readonly Dictionary<StorageCell, byte[]> _storage = [];
    private byte[] _packed = [];

    public bool IsEmpty => _accounts.Count == 0 && _storage.Count == 0;

    public void Balance(Address address, in UInt256 after)
    {
        ref AccountChange change = ref ChangeFor(address);
        change.Balance = after;
        change.HasBalance = true;
    }

    public void Nonce(Address address, in UInt256 after)
    {
        ref AccountChange change = ref ChangeFor(address);
        change.Nonce = after;
        change.HasNonce = true;
    }

    public void Code(Address address, ReadOnlySpan<byte> after)
    {
        ref AccountChange change = ref ChangeFor(address);
        change.CodeHash = after.Length == 0 ? ValueKeccak.OfAnEmptyString : ValueKeccak.Compute(after);
        change.HasCodeHash = true;
    }

    public void Deleted(Address address) => ChangeFor(address).IsDeleted = true;

    public void StorageCleared(Address address) => ChangeFor(address).IsStorageCleared = true;

    public void Storage(in StorageCell cell, byte[]? after) => _storage[cell] = after ?? [];

    public ReadOnlySpan<byte> Pack()
    {
        int capacity = _accounts.Count * ChangesetCodec.MaxAccountEntryLength + _storage.Count * ChangesetCodec.MaxStorageEntryLength;
        if (_packed.Length < capacity)
        {
            byte[] previous = _packed;
            _packed = ArrayPool<byte>.Shared.Rent(capacity);
            if (previous.Length > 0) ArrayPool<byte>.Shared.Return(previous);
        }

        int position = 0;
        Span<byte> balance = stackalloc byte[Hash256.Size];
        Span<byte> nonce = stackalloc byte[Hash256.Size];
        foreach (ref readonly AccountChange change in CollectionsMarshal.AsSpan(_accounts))
        {
            position += ChangesetCodec.WriteAccount(
                _packed.AsSpan(position),
                change.Address,
                change.HasBalance ? Trim(change.Balance, balance) : [],
                change.HasNonce ? Trim(change.Nonce, nonce) : [],
                change.HasCodeHash ? change.CodeHash.Bytes : [],
                change.IsDeleted,
                change.IsStorageCleared);
        }

        Span<byte> index = stackalloc byte[Hash256.Size];
        foreach ((StorageCell cell, byte[] value) in _storage)
        {
            cell.Index.ToBigEndian(index);
            position += ChangesetCodec.WriteStorage(_packed.AsSpan(position), cell.Address, index.WithoutLeadingZeros(), value.WithoutLeadingZeros());
        }

        return _packed.AsSpan(0, position);
    }

    private static ReadOnlySpan<byte> Trim(in UInt256 value, Span<byte> buffer)
    {
        value.ToBigEndian(buffer);
        return buffer.WithoutLeadingZeros();
    }

    /// <summary>Clears the collector, keeping its list and dictionaries warm for the next transaction in this position.
    /// What grew past what a transaction normally writes is let go of instead: the capture lives as long as the process
    /// and holds one collector per position, so retaining every position's high-water mark would be unbounded in
    /// practice.</summary>
    public void Reset()
    {
        _accountIndexes.Clear();
        _accounts.Clear();
        // One check for both: the index grows through primes and the list doubles, so separate checks would trim the
        // index at an account count the list still keeps.
        if (_accounts.Capacity > MaxRetainedAccounts)
        {
            _accountIndexes.TrimExcess();
            _accounts.TrimExcess();
        }
        _storage.Clear();
        if (_storage.EnsureCapacity(0) > MaxRetainedEntries) _storage.TrimExcess();
        if (_packed.Length <= MaxRetainedPacked) return;

        ArrayPool<byte>.Shared.Return(_packed);
        _packed = [];
    }

    public void Release()
    {
        Reset();
        if (_packed.Length == 0) return;

        ArrayPool<byte>.Shared.Return(_packed);
        _packed = [];
    }

    private ref AccountChange ChangeFor(Address address)
    {
        ref int index = ref CollectionsMarshal.GetValueRefOrAddDefault(_accountIndexes, address, out bool existed);
        if (!existed)
        {
            index = _accounts.Count;
            _accounts.Add(new AccountChange { Address = address });
        }

        return ref CollectionsMarshal.AsSpan(_accounts)[index];
    }

    private struct AccountChange
    {
        public Address Address;
        public UInt256 Balance;
        public UInt256 Nonce;
        public ValueHash256 CodeHash;
        public bool HasBalance;
        public bool HasNonce;
        public bool HasCodeHash;
        public bool IsDeleted;
        public bool IsStorageCleared;
    }
}
