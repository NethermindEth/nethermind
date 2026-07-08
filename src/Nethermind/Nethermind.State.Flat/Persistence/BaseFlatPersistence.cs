// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Extensions;
using Nethermind.Serialization.Rlp;

namespace Nethermind.State.Flat.Persistence;

/// <summary>
/// Common persistence logic for flat state storage. Uses 2 database columns:
/// - State: Account data keyed by truncated address hash (20 bytes)
/// - Storage: Contract storage keyed by split address hash + slot hash (52 bytes)
///
/// For storage, the address hash is split: first 4 bytes as prefix, remaining 16 bytes as suffix.
/// This helps RocksDB's comparator skip bytes during comparison and enables index shortening,
/// reducing memory usage. The tradeoff is that SelfDestruct must verify the 16-byte suffix.
///
/// <code>
/// ┌─────────────────────────────────────────────────────────────────────────────────────────────────────────────┐
/// │ State Key (Account)                                                                       Total: 20 bytes  │
/// ├─────────────────────────────────────────────────────────────────────────────────────────────────────────────┤
/// │ Bytes 0-19                                                                                                 │
/// │ AddressHash[0..20]                                                                                         │
/// └─────────────────────────────────────────────────────────────────────────────────────────────────────────────┘
///
/// ┌─────────────────────────────────────────────────────────────────────────────────────────────────────────────┐
/// │ Storage Key                                                                               Total: 52 bytes  │
/// ├──────────────────────────┬────────────────────────────────────────┬─────────────────────────────────────────┤
/// │ Bytes 0-3                │ Bytes 4-35                             │ Bytes 36-51                             │
/// │ AddressHash[0..4]        │ SlotHash[0..32]                        │ AddressHash[4..20]                      │
/// └──────────────────────────┴────────────────────────────────────────┴─────────────────────────────────────────┘
/// </code>
/// </summary>
public static class BaseFlatPersistence
{
    private const int AccountKeyLength = 20;

    private const int StoragePrefixPortion = BasePersistence.StoragePrefixPortion;
    private const int StorageSlotKeySize = 32;
    private const int StoragePostfixPortion = 16;
    private const int StorageKeyLength = StoragePrefixPortion + StorageSlotKeySize + StoragePostfixPortion;

    // Largest RLP encoding of a slot value: a 32-byte string is a 1-byte prefix (0xa0) plus 32 bytes.
    private const int RlpSlotValueBufferSize = SlotValue.ByteCount + 1;

    private static ReadOnlySpan<byte> EncodeAccountKeyHashed(Span<byte> buffer, in ValueHash256 address)
    {
        address.Bytes[..AccountKeyLength].CopyTo(buffer);
        return buffer[..AccountKeyLength];
    }

    private static ReadOnlySpan<byte> EncodeStorageKeyHashedWithShortPrefix(Span<byte> buffer, in ValueHash256 addrHash, in ValueHash256 slotHash)
    {
        // So we store the key with only a small part of the addr early then put the rest at the end.
        // This helps with rocksdb comparator skipping 16 bytes during comparison, and with index shortening, which reduces
        // memory usage. The downside is that during selfdestruct, it will need to double-check the 16 byte postfix.
        // <4-byte-address><32-byte-slot><16-byte-address>
        addrHash.Bytes[..StoragePrefixPortion].CopyTo(buffer);
        slotHash.Bytes.CopyTo(buffer[StoragePrefixPortion..(StoragePrefixPortion + StorageSlotKeySize)]);
        addrHash.Bytes[StoragePrefixPortion..(StoragePrefixPortion + StoragePostfixPortion)].CopyTo(buffer[(StoragePrefixPortion + StorageSlotKeySize)..]);

        return buffer[..StorageKeyLength];
    }

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowSlotValueTooLong(int length, bool rlpWrapSlots) =>
        throw new InvalidConfigurationException(
            $"Flat DB storage slot value is {length} bytes, exceeding the {SlotValue.ByteCount}-byte maximum " +
            $"(rlpWrapSlots={rlpWrapSlots}). The slot-encoding metadata is likely missing or mismatched " +
            $"(RLP-wrapped values read as raw). Re-sync the flat DB to recover.", -1);

    public readonly struct Reader(
        ISortedKeyValueStore state,
        ISortedKeyValueStore storage,
        bool isPreimageMode = false,
        bool rlpWrapSlots = false
    ) : BasePersistence.IHashedFlatReader
    {
        public bool IsPreimageMode => isPreimageMode;

        public int GetAccount(in ValueHash256 address, Span<byte> outBuffer)
        {
            ReadOnlySpan<byte> key = EncodeAccountKeyHashed(stackalloc byte[AccountKeyLength], address);
            return state.Get(key, outBuffer);
        }

        [SkipLocalsInit]
        public bool TryGetStorage(in ValueHash256 address, in ValueHash256 slot, ref SlotValue outValue)
        {
            ReadOnlySpan<byte> storageKey = EncodeStorageKeyHashedWithShortPrefix(stackalloc byte[StorageKeyLength], address, slot);

            Span<byte> buffer = stackalloc byte[RlpSlotValueBufferSize];
            int resultSize = GetStorageBuffer(storageKey, buffer);
            if (resultSize == 0) return false;

            ReadOnlySpan<byte> value = buffer[..resultSize];
            if (rlpWrapSlots)
            {
                RlpReader ctx = new(value);
                value = ctx.DecodeByteArraySpan();
            }

            // The value was read into a RlpSlotValueBufferSize-byte buffer, so len is at most that size; this
            // guard catches a 33-byte RLP-wrapped slot mistakenly read as raw (len 33 > 32), which would
            // otherwise underflow the unchecked InitBlock below into a multi-GB wild memset.
            int len = value.Length;
            if (len > SlotValue.ByteCount) ThrowSlotValueTooLong(len, rlpWrapSlots);

            // len is now guaranteed <= SlotValue.ByteCount, so the unchecked writes below stay in bounds.
            // This writes the variable-length DB value into the end of the 32-byte struct.
            if (len == SlotValue.ByteCount)
            {
                outValue = Unsafe.As<byte, SlotValue>(ref MemoryMarshal.GetReference(value));
            }
            else
            {
                ref byte destBase = ref Unsafe.As<SlotValue, byte>(ref outValue);

                // Zero-initialize the leading bytes before copying the value
                Unsafe.InitBlockUnaligned(ref destBase, 0, (uint)(SlotValue.ByteCount - len));

                ref byte destPtr = ref Unsafe.Add(ref destBase, SlotValue.ByteCount - len);

                Unsafe.CopyBlockUnaligned(
                    ref destPtr,
                    ref MemoryMarshal.GetReference(value),
                    (uint)len);
            }

            return true;
        }

        private int GetStorageBuffer(ReadOnlySpan<byte> key, Span<byte> outBuffer) => storage.Get(key, outBuffer);

        public IPersistence.IFlatIterator CreateAccountIterator(in ValueHash256 startKey, in ValueHash256 endKey)
        {
            // Need to copy to arrays since spans from Value.Bytes might not survive the call
            byte[] start = new byte[AccountKeyLength];
            startKey.Bytes[..AccountKeyLength].CopyTo(start);

            byte[] end = new byte[AccountKeyLength];
            endKey.Bytes[..AccountKeyLength].CopyTo(end);

            return new AccountIterator(state.GetViewBetween(start, end));
        }

        [SkipLocalsInit]
        public IPersistence.IFlatIterator CreateStorageIterator(in ValueHash256 accountKey, in ValueHash256 startSlotKey, in ValueHash256 endSlotKey)
        {
            // Storage key layout: <4-byte-addr><32-byte-slot><16-byte-addr>
            // We need to iterate all keys with the same 4-byte prefix and 16-byte suffix
            Span<byte> firstKey = stackalloc byte[StorageKeyLength];
            Span<byte> lastKey = stackalloc byte[StorageKeyLength + 1];
            EncodeStorageKeyHashedWithShortPrefix(firstKey, accountKey, startSlotKey);
            EncodeStorageKeyHashedWithShortPrefix(lastKey[..StorageKeyLength], accountKey, endSlotKey);
            lastKey[StorageKeyLength] = 0; // Exclusive upper bound

            return new StorageIterator(
                storage.GetViewBetween(firstKey, lastKey),
                accountKey.Bytes[StoragePrefixPortion..(StoragePrefixPortion + StoragePostfixPortion)].ToArray(),
                rlpWrapSlots);
        }
    }

    public struct AccountIterator(ISortedView view) : IPersistence.IFlatIterator
    {
        private ValueHash256 _currentKey = default;
        private byte[]? _currentValue = null;

        public bool MoveNext()
        {
            if (!view.MoveNext()) return false;

            // Account keys are 20 bytes (truncated hash)
            if (view.CurrentKey.Length != AccountKeyLength) return MoveNext();

            // Build 32-byte ValueHash256 from 20-byte key (zero-padded)
            _currentKey = ValueKeccak.Zero;
            view.CurrentKey.CopyTo(_currentKey.BytesAsSpan);
            _currentValue = view.CurrentValue.ToArray();
            return true;
        }

        public ValueHash256 CurrentKey => _currentKey;
        public ReadOnlySpan<byte> CurrentValue => _currentValue;

        public void Dispose() => view.Dispose();
    }

    public struct StorageIterator(ISortedView view, byte[] addressSuffix, bool rlpWrapSlots) : IPersistence.IFlatIterator
    {
        // 16-byte suffix to match
        private ValueHash256 _currentKey = default;
        private byte[]? _currentValue = null;

        public bool MoveNext()
        {
            while (view.MoveNext())
            {
                // Storage keys are 52 bytes: <4-byte-addr><32-byte-slot><16-byte-addr>
                if (view.CurrentKey.Length != StorageKeyLength) continue;

                // Verify the 16-byte address suffix matches
                if (!Bytes.AreEqual(view.CurrentKey[(StoragePrefixPortion + StorageSlotKeySize)..], addressSuffix))
                    continue;

                // Extract the 32-byte slot hash from the middle of the key
                _currentKey = new ValueHash256(view.CurrentKey.Slice(StoragePrefixPortion, StorageSlotKeySize));
                ReadOnlySpan<byte> slotValue = rlpWrapSlots
                    ? new RlpReader(view.CurrentValue).DecodeByteArraySpan()
                    : view.CurrentValue;
                // Mirror TryGetStorage: a slot value over 32 bytes means the encoding is mismatched (e.g. a
                // marker-less DB read as raw). Fail loudly here too, rather than handing snap-sync healing a
                // bad value that would build wrong trie nodes.
                if (slotValue.Length > SlotValue.ByteCount) ThrowSlotValueTooLong(slotValue.Length, rlpWrapSlots);
                _currentValue = slotValue.ToArray();
                return true;
            }
            return false;
        }

        public ValueHash256 CurrentKey => _currentKey;
        public ReadOnlySpan<byte> CurrentValue => _currentValue;

        public void Dispose() => view.Dispose();
    }

    public struct WriteBatch(
        ISortedKeyValueStore stateSnap,
        ISortedKeyValueStore storageSnap,
        IWriteBatch state,
        IWriteBatch storage,
        WriteFlags flags,
        bool rlpWrapSlots = false
    ) : BasePersistence.IHashedFlatWriteBatch
    {
        [SkipLocalsInit]
        public void SelfDestruct(in ValueHash256 accountPath)
        {
            Span<byte> firstKey = stackalloc byte[StoragePrefixPortion];
            Span<byte> lastKey = stackalloc byte[StorageKeyLength + 1];
            BasePersistence.CreateStorageRange(accountPath.Bytes, firstKey, lastKey);
            BasePersistence.DeleteMatchingKeys(storageSnap, storage, firstKey, lastKey,
                StoragePrefixPortion + StorageSlotKeySize, accountPath.Bytes[StoragePrefixPortion..(StoragePrefixPortion + StoragePostfixPortion)]);
        }

        public void RemoveAccount(in ValueHash256 addrHash)
        {
            ReadOnlySpan<byte> key = addrHash.Bytes[..AccountKeyLength];
            state.Remove(key);
        }

        [SkipLocalsInit]
        public void SetStorage(in ValueHash256 addrHash, in ValueHash256 slotHash, in SlotValue? slot)
        {
            ReadOnlySpan<byte> theKey = EncodeStorageKeyHashedWithShortPrefix(stackalloc byte[StorageKeyLength], addrHash, slotHash);

            if (slot.HasValue)
            {
                ReadOnlySpan<byte> withoutLeadingZeros = slot.Value.AsSpan.WithoutLeadingZeros();
                if (rlpWrapSlots)
                {
                    Span<byte> rlpBuffer = stackalloc byte[RlpSlotValueBufferSize];
                    int written = Rlp.Encode(withoutLeadingZeros, rlpBuffer);
                    storage.PutSpan(theKey, rlpBuffer[..written], flags);
                }
                else
                {
                    storage.PutSpan(theKey, withoutLeadingZeros, flags);
                }
            }
            else
            {
                storage.Remove(theKey);
            }
        }

        [SkipLocalsInit]
        public void SetStorageEncoded(in ValueHash256 addrHash, in ValueHash256 slotHash, scoped ReadOnlySpan<byte> rlpValue)
        {
            // The input is the trie leaf value, i.e. RLP(stripped), which is byte-identical to our on-disk format
            // only when wrapping is on. In raw mode there is no verbatim shortcut, so this path is unsupported.
            if (!rlpWrapSlots) throw new NotSupportedException("Encoded slot writes require RLP slot wrapping");

            ReadOnlySpan<byte> theKey = EncodeStorageKeyHashedWithShortPrefix(stackalloc byte[StorageKeyLength], addrHash, slotHash);

            // The bytes are stored verbatim — no decode + re-encode round-trip. The single DecodeByteArraySpan
            // call validates canonical form and bounds the item exactly (trimming any trailing bytes).
            RlpReader ctx = new(rlpValue);
            ctx.DecodeByteArraySpan();
            storage.PutSpan(theKey, rlpValue[..ctx.Position], flags);
        }

        public void SetAccount(in ValueHash256 addrHash, ReadOnlySpan<byte> account)
        {
            ReadOnlySpan<byte> key = addrHash.Bytes[..AccountKeyLength];
            state.PutSpan(key, account, flags);
        }

        [SkipLocalsInit]
        public void DeleteAccountRange(in ValueHash256 fromPath, in ValueHash256 toPath)
        {
            Span<byte> firstKey = stackalloc byte[AccountKeyLength];
            Span<byte> lastKey = stackalloc byte[AccountKeyLength + 1]; // +1 for exclusive upper bound
            fromPath.Bytes[..AccountKeyLength].CopyTo(firstKey);
            toPath.Bytes[..AccountKeyLength].CopyTo(lastKey);
            lastKey[AccountKeyLength] = 0;
            BasePersistence.DeleteMatchingKeys(stateSnap, state, firstKey, lastKey, AccountKeyLength);
        }

        [SkipLocalsInit]
        public void DeleteStorageRange(in ValueHash256 addressHash, in ValueHash256 fromPath, in ValueHash256 toPath)
        {
            Span<byte> firstKey = stackalloc byte[StorageKeyLength];
            Span<byte> lastKey = stackalloc byte[StorageKeyLength + 1];
            EncodeStorageKeyHashedWithShortPrefix(firstKey, addressHash, fromPath);
            EncodeStorageKeyHashedWithShortPrefix(lastKey[..StorageKeyLength], addressHash, toPath);
            lastKey[StorageKeyLength] = 0;
            BasePersistence.DeleteMatchingKeys(storageSnap, storage, firstKey, lastKey,
                StoragePrefixPortion + StorageSlotKeySize, addressHash.Bytes[StoragePrefixPortion..(StoragePrefixPortion + StoragePostfixPortion)]);
        }
    }
}
