// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;

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
    private const int StateKeyPrefixLength = 20;
    private const int StorageHashPrefixLength = 20; // Store prefix of the 32 byte of the storage. Reduces index size.
    private const int StorageSlotKeySize = 32;
    private const int StorageKeyLength = StorageHashPrefixLength + StorageSlotKeySize;
    private const int StoragePrefixPortion = 4;

    private static ReadOnlySpan<byte> EncodeAccountKeyHashed(Span<byte> buffer, in ValueHash256 address)
    {
        address.Bytes[..StorageHashPrefixLength].CopyTo(buffer);
        return buffer[..StateKeyPrefixLength];
    }

    private static ReadOnlySpan<byte> EncodeStorageKeyHashedWithShortPrefix(Span<byte> buffer, in ValueHash256 addrHash, in ValueHash256 slotHash)
    {
        // So we store the key with only small part of the addr early then put the rest at the end.
        // This helps with rocksdb comparator skipping 16 byte during comparison, and with index shortening, which reduces
        // memory usage. The downside is that during selfdestruct, it will need to double check the 16 byte postfix.
        // <4-byte-address><32-byte-slot><16-byte-address>
        addrHash.Bytes[..StoragePrefixPortion].CopyTo(buffer);
        slotHash.Bytes.CopyTo(buffer[StoragePrefixPortion..(StoragePrefixPortion + StorageSlotKeySize)]);
        addrHash.Bytes[StoragePrefixPortion..StorageHashPrefixLength].CopyTo(buffer[(StoragePrefixPortion + StorageSlotKeySize)..]);

        return buffer[..StorageKeyLength];
    }

    public struct Reader(
        IReadOnlyKeyValueStore state,
        IReadOnlyKeyValueStore storage
    ) : BasePersistence.IHashedFlatReader
    {

        public int GetAccount(in ValueHash256 address, Span<byte> outBuffer)
        {
            ReadOnlySpan<byte> key = EncodeAccountKeyHashed(stackalloc byte[StateKeyPrefixLength], address);
            return state.Get(key, outBuffer);
        }

        public bool TryGetStorage(in ValueHash256 address, in ValueHash256 slot, ref SlotValue outValue)
        {
            ReadOnlySpan<byte> storageKey = EncodeStorageKeyHashedWithShortPrefix(stackalloc byte[StorageKeyLength], address, slot);

            Span<byte> buffer = stackalloc byte[40];
            int resultSize = GetStorageBuffer(storageKey, buffer);
            if (resultSize == 0) return false;

            Span<byte> value = buffer[..resultSize];

            // AI said: Use Unsafe to bypass the 'Slice' bounds check and property access
            // This writes the variable-length DB value into the end of the 32-byte struct
            unsafe
            {
                int len = value.Length;
                if (len == SlotValue.ByteCount)
                {
                    outValue = Unsafe.As<byte, SlotValue>(ref MemoryMarshal.GetReference(value));
                }
                else
                {
                    ref byte destBase = ref Unsafe.As<SlotValue, byte>(ref outValue);
                    ref byte destPtr = ref Unsafe.Add(ref destBase, SlotValue.ByteCount - len);

                    Unsafe.CopyBlockUnaligned(
                        ref destPtr,
                        ref MemoryMarshal.GetReference(value),
                        (uint)len);
                }
            }
            return true;
        }

        private int GetStorageBuffer(ReadOnlySpan<byte> key, Span<byte> outBuffer)
        {
            return storage.Get(key, outBuffer);
        }
    }

    public struct WriteBatch(
        ISortedKeyValueStore storageSnap,
        IWriteOnlyKeyValueStore state,
        IWriteOnlyKeyValueStore storage,
        WriteFlags flags
    ) : BasePersistence.IHashedFlatWriteBatch
    {
        public int SelfDestruct(in ValueHash256 accountPath)
        {
            Span<byte> firstKey = stackalloc byte[StoragePrefixPortion]; // Because slot 0 is a thing, its just the address prefix.
            Span<byte> lastKey = stackalloc byte[StorageKeyLength + 1]; // The +1 is because upper bound is exclusive
            firstKey.Fill(0x00);
            lastKey.Fill(0xff);
            accountPath.Bytes[..StoragePrefixPortion].CopyTo(firstKey);
            accountPath.Bytes[..StoragePrefixPortion].CopyTo(lastKey);

            int removedEntry = 0;
            using (ISortedView storageReader = storageSnap.GetViewBetween(firstKey, lastKey))
            {
                IWriteOnlyKeyValueStore? storageWriter = storage;
                while (storageReader.MoveNext())
                {
                    // FlatInTrie
                    if (storageReader.CurrentKey.Length != StorageKeyLength) continue;

                    // If we have storage prefix portion, we need to double check that the last 16 byte match.
                    if (Bytes.AreEqual(storageReader.CurrentKey[(StoragePrefixPortion + StorageSlotKeySize)..], accountPath.Bytes[StoragePrefixPortion..(StorageHashPrefixLength)]))
                    {
                        storageWriter.Remove(storageReader.CurrentKey);
                        removedEntry++;
                    }
                }
            }

            return removedEntry;
        }

        public void RemoveAccount(in ValueHash256 addrHash)
        {
            ReadOnlySpan<byte> key = addrHash.Bytes[..StateKeyPrefixLength];
            state.Remove(key);
        }

        public void SetStorage(in ValueHash256 addrHash, in ValueHash256 slotHash, in SlotValue? slot)
        {
            ReadOnlySpan<byte> theKey = EncodeStorageKeyHashedWithShortPrefix(stackalloc byte[StorageKeyLength], addrHash, slotHash);

            if (slot.HasValue)
            {
                ReadOnlySpan<byte> withoutLeadingZeros = slot.Value.AsSpan.WithoutLeadingZeros();
                storage.PutSpan(theKey, withoutLeadingZeros, flags);
            }
            else
            {
                storage.Remove(theKey);
            }
        }

        public void SetAccount(in ValueHash256 addrHash, ReadOnlySpan<byte> account)
        {
            ReadOnlySpan<byte> key = addrHash.Bytes[..StateKeyPrefixLength];
            state.PutSpan(key, account, flags);
        }
    }
}
