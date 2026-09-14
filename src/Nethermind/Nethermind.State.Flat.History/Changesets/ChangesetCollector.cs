// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;

namespace Nethermind.State.Flat.History.Changesets;

/// <summary>Accumulates one transaction's writes, keeping only the last value written to each account field and
/// each slot, then packs them once.</summary>
internal sealed class ChangesetCollector
{
    private readonly Dictionary<AddressAsKey, AccountChange> _accounts = [];
    private readonly Dictionary<StorageCell, byte[]> _storage = [];
    private byte[] _packed = [];
    private int _packedLength;

    public bool IsEmpty => _accounts.Count == 0 && _storage.Count == 0;

    public void Balance(Address address, in UInt256 after) => Field(address).Balance = after.ToBigEndian().WithoutLeadingZeros().ToArray();

    public void Nonce(Address address, in UInt256 after) => Field(address).Nonce = after.ToBigEndian().WithoutLeadingZeros().ToArray();

    public void Code(Address address, byte[] after) => Field(address).CodeHash = after.Length == 0 ? Keccak.OfAnEmptyString : Keccak.Compute(after);

    public void Deleted(Address address) => Field(address).IsDeleted = true;

    public void StorageCleared(Address address) => Field(address).IsStorageCleared = true;

    public void Storage(in StorageCell cell, byte[]? after) => _storage[cell] = after ?? [];

    public ReadOnlySpan<byte> Pack()
    {
        int capacity = _accounts.Count * ChangesetCodec.MaxAccountEntryLength + _storage.Count * ChangesetCodec.MaxStorageEntryLength;
        if (_packed.Length < capacity)
        {
            if (_packed.Length > 0) ArrayPool<byte>.Shared.Return(_packed);
            _packed = ArrayPool<byte>.Shared.Rent(capacity);
        }

        int position = 0;
        foreach ((AddressAsKey address, AccountChange change) in _accounts)
        {
            position += ChangesetCodec.WriteAccount(
                _packed.AsSpan(position),
                address.Value,
                change.Balance ?? [],
                change.Nonce ?? [],
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

        _packedLength = position;
        return _packed.AsSpan(0, _packedLength);
    }

    public void Reset()
    {
        _accounts.Clear();
        _storage.Clear();
        _packedLength = 0;
    }

    public void Release()
    {
        Reset();
        if (_packed.Length == 0) return;

        ArrayPool<byte>.Shared.Return(_packed);
        _packed = [];
    }

    private AccountChange Field(Address address)
    {
        ref AccountChange? change = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(_accounts, address, out bool existed);
        if (!existed) change = new AccountChange();
        return change!;
    }

    private sealed class AccountChange
    {
        public byte[]? Balance { get; set; }

        public byte[]? Nonce { get; set; }

        public Hash256? CodeHash { get; set; }

        public bool IsDeleted { get; set; }

        public bool IsStorageCleared { get; set; }
    }
}
