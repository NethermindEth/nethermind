// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.State.Flat.History.Changesets;

/// <summary>One transaction's writes, packed. Entries carry only significant bytes, so a zeroed slot index or a
/// cleared value costs one length byte.</summary>
internal static class ChangesetCodec
{
    public const byte AccountKind = 0;
    public const byte StorageKind = 1;

    public const int MaxAccountValueLength = 128;
    public const int MaxAccountEntryLength = 1 + Address.Size + 1 + MaxAccountValueLength;
    public const int MaxStorageEntryLength = 1 + Address.Size + 1 + Hash256.Size + 1 + Hash256.Size;

    public static int WriteAccount(Span<byte> destination, Address address, scoped ReadOnlySpan<byte> value)
    {
        if (value.Length > MaxAccountValueLength) ThrowValueTooLong(value.Length, MaxAccountValueLength);

        destination[0] = AccountKind;
        address.Bytes.CopyTo(destination[1..]);
        int position = 1 + Address.Size;
        destination[position++] = (byte)value.Length;
        value.CopyTo(destination[position..]);
        return position + value.Length;
    }

    public static int WriteStorage(Span<byte> destination, Address address, scoped ReadOnlySpan<byte> index, scoped ReadOnlySpan<byte> value)
    {
        if (index.Length > Hash256.Size) ThrowValueTooLong(index.Length, Hash256.Size);
        if (value.Length > Hash256.Size) ThrowValueTooLong(value.Length, Hash256.Size);

        destination[0] = StorageKind;
        address.Bytes.CopyTo(destination[1..]);
        int position = 1 + Address.Size;
        destination[position++] = (byte)index.Length;
        index.CopyTo(destination[position..]);
        position += index.Length;
        destination[position++] = (byte)value.Length;
        value.CopyTo(destination[position..]);
        return position + value.Length;
    }

    public static Enumerator Read(ReadOnlySpan<byte> changeset) => new(changeset);

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
        }

        public byte Kind { get; private set; }

        public ReadOnlySpan<byte> Address { get; private set; }

        public ReadOnlySpan<byte> Index { get; private set; }

        public ReadOnlySpan<byte> Value { get; private set; }

        public bool MoveNext()
        {
            if (_position >= _changeset.Length) return false;

            Kind = _changeset[_position++];
            if (Kind is not (AccountKind or StorageKind)) ThrowMalformed();

            Address = Take(Nethermind.Core.Address.Size);
            Index = Kind == StorageKind ? TakeLengthPrefixed() : default;
            Value = TakeLengthPrefixed();
            return true;
        }

        private ReadOnlySpan<byte> TakeLengthPrefixed()
        {
            if (_position >= _changeset.Length) ThrowMalformed();

            return Take(_changeset[_position++]);
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
