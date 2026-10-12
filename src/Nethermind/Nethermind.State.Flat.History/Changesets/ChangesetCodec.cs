// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.State.Flat.History.Changesets;

/// <summary>One transaction's writes, packed. An account entry carries only the fields the transaction changed, so
/// the overlay merges them onto the account as of the previous block rather than restating it. Numbers carry only
/// their significant bytes, so a zeroed slot index or a cleared value costs one length byte.</summary>
internal static class ChangesetCodec
{
    public const byte AccountKind = 0;
    public const byte StorageKind = 1;

    public const byte BalanceField = 0x01;
    public const byte NonceField = 0x02;
    public const byte CodeField = 0x04;
    public const byte DeletedField = 0x08;
    public const byte StorageClearedField = 0x10;

    private const int AddressSize = Address.Size;

    public const int MaxAccountEntryLength = 1 + Address.Size + 1 + (1 + Hash256.Size) * 2 + Hash256.Size;
    public const int MaxStorageEntryLength = 1 + Address.Size + 1 + Hash256.Size + 1 + Hash256.Size;

    public static int WriteAccount(
        Span<byte> destination,
        Address address,
        scoped ReadOnlySpan<byte> balance,
        scoped ReadOnlySpan<byte> nonce,
        scoped ReadOnlySpan<byte> codeHash,
        bool deleted,
        bool storageCleared)
    {
        byte fields = 0;
        if (!balance.IsEmpty) fields |= BalanceField;
        if (!nonce.IsEmpty) fields |= NonceField;
        if (!codeHash.IsEmpty) fields |= CodeField;
        if (deleted) fields |= DeletedField;
        if (storageCleared) fields |= StorageClearedField;

        destination[0] = AccountKind;
        address.Bytes.CopyTo(destination[1..]);
        int position = 1 + Address.Size;
        destination[position++] = fields;
        if (!balance.IsEmpty) position = WriteLengthPrefixed(destination, position, balance, Hash256.Size);
        if (!nonce.IsEmpty) position = WriteLengthPrefixed(destination, position, nonce, Hash256.Size);
        if (codeHash.IsEmpty) return position;

        if (codeHash.Length != Hash256.Size) ThrowValueTooLong(codeHash.Length, Hash256.Size);

        codeHash.CopyTo(destination[position..]);
        return position + Hash256.Size;
    }

    public static int WriteStorage(Span<byte> destination, Address address, scoped ReadOnlySpan<byte> index, scoped ReadOnlySpan<byte> value)
    {
        destination[0] = StorageKind;
        address.Bytes.CopyTo(destination[1..]);
        int position = 1 + Address.Size;
        position = WriteLengthPrefixed(destination, position, index, Hash256.Size);
        return WriteLengthPrefixed(destination, position, value, Hash256.Size);
    }

    public static Enumerator Read(ReadOnlySpan<byte> changeset) => new(changeset);

    private static int WriteLengthPrefixed(Span<byte> destination, int position, scoped ReadOnlySpan<byte> value, int limit)
    {
        if (value.Length > limit) ThrowValueTooLong(value.Length, limit);

        destination[position++] = (byte)value.Length;
        value.CopyTo(destination[position..]);
        return position + value.Length;
    }

    private static void ThrowValueTooLong(int length, int limit) =>
        throw new ArgumentOutOfRangeException(nameof(length), length, $"A changeset entry carries at most {limit} bytes.");

    public ref struct Enumerator
    {
        private readonly ReadOnlySpan<byte> _changeset;
        private int _position;

        internal Enumerator(ReadOnlySpan<byte> changeset)
        {
            _changeset = changeset;
            _position = 0;
            Address = default;
            Index = default;
            Value = default;
            Balance = default;
            Nonce = default;
            CodeHash = default;
        }

        public byte Kind { get; private set; }

        public byte Fields { get; private set; }

        public ReadOnlySpan<byte> Address { get; private set; }

        public ReadOnlySpan<byte> Index { get; private set; }

        public ReadOnlySpan<byte> Value { get; private set; }

        public ReadOnlySpan<byte> Balance { get; private set; }

        public ReadOnlySpan<byte> Nonce { get; private set; }

        public ReadOnlySpan<byte> CodeHash { get; private set; }

        public bool Deleted => (Fields & DeletedField) != 0;

        /// <summary>The transaction wiped every slot of the account, whether or not the account itself survived.</summary>
        public bool StorageCleared => (Fields & (StorageClearedField | DeletedField)) != 0;

        public bool MoveNext()
        {
            if (_position >= _changeset.Length) return false;

            Kind = _changeset[_position++];
            Address = Take(AddressSize);
            Fields = 0;
            Index = default;
            Value = default;
            Balance = default;
            Nonce = default;
            CodeHash = default;

            switch (Kind)
            {
                case AccountKind:
                    Fields = TakeByte();
                    if ((Fields & BalanceField) != 0) Balance = TakeLengthPrefixed();
                    if ((Fields & NonceField) != 0)
                    {
                        Nonce = TakeLengthPrefixed();
                        // EIP-2681 caps an account nonce at 2^64-1; larger significant values cannot describe a valid account.
                        if (Nonce.TrimStart((byte)0).Length > sizeof(ulong)) ThrowMalformed();
                    }
                    if ((Fields & CodeField) != 0) CodeHash = Take(Hash256.Size);
                    return true;
                case StorageKind:
                    Index = TakeLengthPrefixed();
                    Value = TakeLengthPrefixed();
                    return true;
                default:
                    ThrowMalformed();
                    return false;
            }
        }

        private byte TakeByte()
        {
            if (_position >= _changeset.Length) ThrowMalformed();

            return _changeset[_position++];
        }

        /// <summary>A written field is never longer than a hash, so a longer one is a corrupt row rather than a
        /// value this codec produced; refusing it here keeps every corrupt shape a malformed row instead of whatever
        /// the field's own parser throws.</summary>
        private ReadOnlySpan<byte> TakeLengthPrefixed()
        {
            byte length = TakeByte();
            if (length > Hash256.Size) ThrowMalformed();

            return Take(length);
        }

        private ReadOnlySpan<byte> Take(int length)
        {
            if (_position + length > _changeset.Length) ThrowMalformed();

            ReadOnlySpan<byte> slice = _changeset.Slice(_position, length);
            _position += length;
            return slice;
        }

        private static void ThrowMalformed() =>
            throw new InvalidDataException("A transaction changeset row is truncated or carries an unknown entry kind.");
    }
}
