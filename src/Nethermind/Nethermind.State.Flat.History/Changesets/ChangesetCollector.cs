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
    private const int MaxRetainedPacked = 64 * 1024;

    private readonly Dictionary<AddressAsKey, AccountChange> _accounts = [];
    private readonly Dictionary<StorageCell, byte[]> _storage = [];
    private byte[] _packed = [];

    public bool IsEmpty => _accounts.Count == 0 && _storage.Count == 0;

    public void Balance(Address address, in UInt256 after) => ChangeFor(address).Balance = after;

    public void Nonce(Address address, in UInt256 after) => ChangeFor(address).Nonce = after;

    public void Code(Address address, byte[] after) => ChangeFor(address).CodeHash = after.Length == 0 ? Keccak.OfAnEmptyString : Keccak.Compute(after);

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
        foreach ((AddressAsKey address, AccountChange change) in _accounts)
        {
            position += ChangesetCodec.WriteAccount(
                _packed.AsSpan(position),
                address.Value,
                Trim(change.Balance, balance),
                Trim(change.Nonce, nonce),
                change.CodeHash is null ? [] : change.CodeHash.Bytes,
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

    private static ReadOnlySpan<byte> Trim(in UInt256? value, Span<byte> buffer)
    {
        if (value is not { } number) return [];

        number.ToBigEndian(buffer);
        return buffer.WithoutLeadingZeros();
    }

    /// <summary>Clears the collector, keeping its dictionaries warm for the next transaction in this position. What
    /// grew past what a transaction normally writes is let go of instead: the capture lives as long as the process and
    /// holds one collector per position, so retaining every position's high-water mark would be unbounded in practice.</summary>
    public void Reset()
    {
        _accounts.Clear();
        if (_accounts.EnsureCapacity(0) > MaxRetainedEntries) _accounts.TrimExcess();
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

    private AccountChange ChangeFor(Address address)
    {
        ref AccountChange? change = ref CollectionsMarshal.GetValueRefOrAddDefault(_accounts, address, out bool existed);
        if (!existed) change = new AccountChange();
        return change!;
    }

    private sealed class AccountChange
    {
        public UInt256? Balance { get; set; }

        public UInt256? Nonce { get; set; }

        public Hash256? CodeHash { get; set; }

        public bool IsDeleted { get; set; }

        public bool IsStorageCleared { get; set; }
    }
}
