// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Utils;
using Nethermind.Int256;

namespace Nethermind.State.Flat.History.Prestate;

/// <summary>One validated pre-state record held in pooled memory. Leases count its readers; the buffers go back to
/// their pools when the last lease ends.</summary>
/// <remarks>Validation builds one offset table: the start of each account, the first slot of each account (plus one
/// end marker), and the start of each slot. A read is a binary search over it, bounded by the record's own counts and
/// never by the pooled array's length.</remarks>
public sealed class PrestateRecord : RefCountingDisposable
{
    private readonly byte[] _buffer;
    private readonly int _length;
    private readonly int[] _index;
    private readonly Account?[] _accounts;
    private readonly int _accountCount;
    private readonly int _slotCount;

    private PrestateRecord(byte[] buffer, int length, int[] index, Account?[] accounts, int accountCount, int slotCount)
    {
        _buffer = buffer;
        _length = length;
        _index = index;
        _accounts = accounts;
        _accountCount = accountCount;
        _slotCount = slotCount;
    }

    public int AccountCount => _accountCount;

    public int SlotCount => _slotCount;

    /// <summary>Memory the record holds, charged to the cache budget.</summary>
    public long ByteSize => _buffer.Length + (long)_index.Length * sizeof(int) + (long)_accounts.Length * IntPtr.Size;

    public ulong BlockNumber => BinaryPrimitives.ReadUInt64LittleEndian(_buffer.AsSpan(PrestateRecordFormat.BlockNumberOffset));

    public ValueHash256 BlockHash => new(_buffer.AsSpan(PrestateRecordFormat.BlockHashOffset, Hash256.Size));

    private ReadOnlySpan<byte> Bytes => _buffer.AsSpan(0, _length);

    private ReadOnlySpan<int> AccountOffsets => _index.AsSpan(0, _accountCount);

    private ReadOnlySpan<int> SlotStarts => _index.AsSpan(_accountCount, _accountCount + 1);

    private ReadOnlySpan<int> SlotOffsets => _index.AsSpan(2 * _accountCount + 1, _slotCount);

    /// <summary>Takes one more lease. False once the last lease has ended.</summary>
    public bool TryLease() => TryAcquireLease();

    /// <summary>Copies and validates <paramref name="data"/>. Null when it is not a well-formed record of
    /// <paramref name="parent"/>.</summary>
    public static PrestateRecord? TryOpen(ReadOnlySpan<byte> data, in StateId parent)
    {
        if (!TryReadCounts(data, parent, out int accountCount, out int slotCount)) return null;

        byte[] buffer = ArrayPool<byte>.Shared.Rent(data.Length);
        int[] index = ArrayPool<int>.Shared.Rent(2 * accountCount + 1 + slotCount);
        data.CopyTo(buffer);
        if (!TryBuildIndex(buffer.AsSpan(0, data.Length), accountCount, slotCount, index))
        {
            ArrayPool<byte>.Shared.Return(buffer);
            ArrayPool<int>.Shared.Return(index);
            return null;
        }

        Account?[] accounts = ArrayPool<Account?>.Shared.Rent(accountCount);
        accounts.AsSpan(0, accountCount).Clear();
        return new PrestateRecord(buffer, data.Length, index, accounts, accountCount, slotCount);
    }

    /// <summary>False when the record does not hold the account value; the caller then reads history. A held absent
    /// account returns true with null.</summary>
    public bool TryGetAccount(Address address, out Account? account)
    {
        account = null;
        int position = FindAccount(address);
        if (position < 0) return false;

        ReadOnlySpan<byte> bytes = Bytes;
        int offset = AccountOffsets[position] + Address.Size;
        byte flags = bytes[offset];
        if ((flags & PrestateRecordFormat.AccountValueNotRecorded) != 0) return false;
        if ((flags & PrestateRecordFormat.AccountExists) == 0) return true;

        account = Volatile.Read(ref _accounts[position]);
        if (account is not null) return true;

        Account created = DecodeAccount(bytes, offset);
        account = Interlocked.CompareExchange(ref _accounts[position], created, null) ?? created;
        return true;
    }

    /// <summary>False when the record does not hold the slot; the caller then reads history.</summary>
    [SkipLocalsInit]
    public bool TryGetSlot(Address address, in UInt256 index, out UInt256 value)
    {
        value = default;
        int position = FindAccount(address);
        if (position < 0) return false;

        ReadOnlySpan<int> slotStarts = SlotStarts;
        int low = slotStarts[position];
        int high = slotStarts[position + 1] - 1;
        if (low > high) return false;

        Span<byte> key = stackalloc byte[32];
        index.ToBigEndian(key);
        ReadOnlySpan<byte> bytes = Bytes;
        ReadOnlySpan<int> slotOffsets = SlotOffsets;
        while (low <= high)
        {
            int middle = low + ((high - low) >> 1);
            int offset = slotOffsets[middle];
            int comparison = bytes.Slice(offset, 32).SequenceCompareTo(key);
            if (comparison == 0)
            {
                int length = bytes[offset + 32];
                value = length == 0 ? default : new UInt256(bytes.Slice(offset + 33, length), isBigEndian: true);
                return true;
            }

            if (comparison < 0) low = middle + 1;
            else high = middle - 1;
        }

        return false;
    }

    private int FindAccount(Address address)
    {
        ReadOnlySpan<byte> bytes = Bytes;
        ReadOnlySpan<byte> key = address.Bytes;
        ReadOnlySpan<int> offsets = AccountOffsets;
        int low = 0;
        int high = offsets.Length - 1;
        while (low <= high)
        {
            int middle = low + ((high - low) >> 1);
            int comparison = bytes.Slice(offsets[middle], Address.Size).SequenceCompareTo(key);
            if (comparison == 0) return middle;
            if (comparison < 0) low = middle + 1;
            else high = middle - 1;
        }

        return -1;
    }

    /// <summary>Called only for a validated, existing account; <paramref name="offset"/> points at its flags.</summary>
    private static Account DecodeAccount(ReadOnlySpan<byte> bytes, int offset)
    {
        byte flags = bytes[offset];
        int position = offset + 1;
        PrestateRecordFormat.TryReadVarInt(bytes, ref position, out ulong nonce);
        int balanceLength = bytes[position++];
        UInt256 balance = balanceLength == 0 ? default : new UInt256(bytes.Slice(position, balanceLength), isBigEndian: true);
        position += balanceLength;

        Hash256 codeHash = Keccak.OfAnEmptyString;
        if ((flags & PrestateRecordFormat.AccountHasCode) != 0)
        {
            codeHash = new Hash256(bytes.Slice(position, Hash256.Size));
            position += Hash256.Size;
        }

        Hash256 storageRoot = Keccak.EmptyTreeHash;
        if ((flags & PrestateRecordFormat.AccountHasStorageRoot) != 0) storageRoot = new Hash256(bytes.Slice(position, Hash256.Size));

        return new Account(nonce, balance, storageRoot, codeHash);
    }

    private static bool TryReadCounts(ReadOnlySpan<byte> data, in StateId parent, out int accountCount, out int slotCount)
    {
        accountCount = 0;
        slotCount = 0;
        if (data.Length < PrestateRecordFormat.HeaderLength) return false;
        if (data[PrestateRecordFormat.VersionOffset] != PrestateRecordFormat.Version) return false;
        if (data[PrestateRecordFormat.FlagsOffset] != PrestateRecordFormat.RecordFlagNonAuthoritativeRoots) return false;
        if (data[2] != 0 || data[3] != 0) return false;
        if (data.Slice(PrestateRecordFormat.ReservedOffset, PrestateRecordFormat.HeaderLength - PrestateRecordFormat.ReservedOffset).ContainsAnyExcept((byte)0)) return false;
        if (BinaryPrimitives.ReadUInt32LittleEndian(data[PrestateRecordFormat.SenderCountOffset..]) != 0) return false;
        if (BinaryPrimitives.ReadUInt64LittleEndian(data[PrestateRecordFormat.ParentNumberOffset..]) != parent.BlockNumber) return false;
        if (!data.Slice(PrestateRecordFormat.ParentRootOffset, Hash256.Size).SequenceEqual(parent.StateRoot.Bytes)) return false;

        uint accounts = BinaryPrimitives.ReadUInt32LittleEndian(data[PrestateRecordFormat.AccountCountOffset..]);
        uint slots = BinaryPrimitives.ReadUInt32LittleEndian(data[PrestateRecordFormat.SlotCountOffset..]);
        long body = data.Length - PrestateRecordFormat.HeaderLength;
        if (accounts * (long)PrestateRecordFormat.MinAccountLength + slots * (long)PrestateRecordFormat.MinSlotLength > body) return false;
        if (slots > 0 && accounts == 0) return false;

        accountCount = (int)accounts;
        slotCount = (int)slots;
        return true;
    }

    /// <summary>One pass over the body: every bound, every flag, strict key order, minimal values, exact end.</summary>
    private static bool TryBuildIndex(ReadOnlySpan<byte> data, int accountCount, int slotCount, Span<int> index)
    {
        Span<int> accountOffsets = index[..accountCount];
        Span<int> slotStarts = index.Slice(accountCount, accountCount + 1);
        Span<int> slotOffsets = index.Slice(2 * accountCount + 1, slotCount);
        int position = PrestateRecordFormat.HeaderLength;
        int slot = 0;
        for (int account = 0; account < accountCount; account++)
        {
            if (data.Length - position < PrestateRecordFormat.MinAccountLength) return false;
            if (account > 0 && data.Slice(position, Address.Size).SequenceCompareTo(data.Slice(accountOffsets[account - 1], Address.Size)) <= 0) return false;

            accountOffsets[account] = position;
            slotStarts[account] = slot;
            position += Address.Size;
            byte flags = data[position++];
            if (!TryCheckAccount(data, flags, ref position)) return false;
            if ((flags & PrestateRecordFormat.AccountHasSlots) == 0) continue;

            if (!PrestateRecordFormat.TryReadVarInt(data, ref position, out ulong count) || count == 0 || count > (ulong)(slotCount - slot)) return false;
            int end = slot + (int)count;
            for (; slot < end; slot++)
            {
                if (data.Length - position < PrestateRecordFormat.MinSlotLength) return false;
                if (slot > slotStarts[account] && data.Slice(position, 32).SequenceCompareTo(data.Slice(slotOffsets[slot - 1], 32)) <= 0) return false;

                slotOffsets[slot] = position;
                position += 32;
                if (!TrySkipMinimalValue(data, ref position)) return false;
            }
        }

        slotStarts[accountCount] = slot;
        return slot == slotCount && position == data.Length;
    }

    private static bool TryCheckAccount(ReadOnlySpan<byte> data, byte flags, ref int position)
    {
        if ((flags & ~PrestateRecordFormat.KnownAccountFlags) != 0) return false;

        const byte valueFlags = PrestateRecordFormat.AccountExists | PrestateRecordFormat.AccountHasCode | PrestateRecordFormat.AccountHasStorageRoot;
        if ((flags & PrestateRecordFormat.AccountValueNotRecorded) != 0)
        {
            return (flags & valueFlags) == 0 && (flags & PrestateRecordFormat.AccountHasSlots) != 0;
        }

        if ((flags & PrestateRecordFormat.AccountExists) == 0) return (flags & valueFlags) == 0;

        if (!PrestateRecordFormat.TryReadVarInt(data, ref position, out _)) return false;
        if (!TrySkipMinimalValue(data, ref position)) return false;
        if ((flags & PrestateRecordFormat.AccountHasCode) != 0 && !TrySkipHash(data, ref position, Keccak.OfAnEmptyString)) return false;
        return (flags & PrestateRecordFormat.AccountHasStorageRoot) == 0 || TrySkipHash(data, ref position, Keccak.EmptyTreeHash);
    }

    private static bool TrySkipMinimalValue(ReadOnlySpan<byte> data, ref int position)
    {
        if ((uint)position >= (uint)data.Length) return false;
        int length = data[position++];
        if (length > 32 || data.Length - position < length) return false;
        if (length > 0 && data[position] == 0) return false;
        position += length;
        return true;
    }

    /// <summary>An empty hash is written as a cleared flag, never as bytes, so seeing it here means a malformed record.</summary>
    private static bool TrySkipHash(ReadOnlySpan<byte> data, ref int position, Hash256 empty)
    {
        if (data.Length - position < Hash256.Size) return false;
        if (data.Slice(position, Hash256.Size).SequenceEqual(empty.Bytes)) return false;
        position += Hash256.Size;
        return true;
    }

    protected override void CleanUp()
    {
        ArrayPool<byte>.Shared.Return(_buffer);
        ArrayPool<int>.Shared.Return(_index);
        ArrayPool<Account?>.Shared.Return(_accounts, clearArray: true);
    }
}
