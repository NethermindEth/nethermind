// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;

namespace Nethermind.State.Flat.Persistence;

public static class BaseFlatPersistence
{
    private const int StateKeyPrefixLength = 20;
    private const int StorageHashPrefixLength = 20; // Store prefix of the 32 byte of the storage. Reduces index size.
    private const int StorageSlotKeySize = 32;
    private const int StorageKeyLength = StorageHashPrefixLength + StorageSlotKeySize;

    private static ReadOnlySpan<byte> EncodeAccountKeyHashed(Span<byte> buffer, in ValueHash256 address)
    {
        address.Bytes[..StorageHashPrefixLength].CopyTo(buffer);
        return buffer[..StateKeyPrefixLength];
    }

    private static ReadOnlySpan<byte> EncodeStorageKeyHashed(Span<byte> buffer, in ValueHash256 addrHash, in ValueHash256 slotHash)
    {
        addrHash.Bytes[..StorageHashPrefixLength].CopyTo(buffer);
        slotHash.Bytes.CopyTo(buffer[StorageHashPrefixLength..(StorageHashPrefixLength + StorageSlotKeySize)]);
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
            ReadOnlySpan<byte> span = state.GetSpan(key);
            try
            {
                span.CopyTo(outBuffer);
                return span.Length;
            }
            finally
            {
                state.DangerousReleaseMemory(span);
            }
        }

        public int GetStorage(in ValueHash256 address, in ValueHash256 slot, Span<byte> outBuffer)
        {
            Span<byte> keySpan = stackalloc byte[StorageKeyLength];
            ReadOnlySpan<byte> storageKey = EncodeStorageKeyHashed(keySpan, address, slot);
            Span<byte> value = storage.GetSpan(storageKey);
            try
            {
                if (value.IsNullOrEmpty())
                {
                    return 0;
                }

                value.CopyTo(outBuffer);
                return value.Length;
            }
            finally
            {
                storage.DangerousReleaseMemory(value);
            }
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
            Span<byte> firstKey = stackalloc byte[StorageHashPrefixLength]; // Because slot 0 is a thing, its just the address prefix.
            Span<byte> lastKey = stackalloc byte[StorageKeyLength];
            firstKey.Fill(0x00);
            lastKey.Fill(0xff);
            accountPath.Bytes[..StorageHashPrefixLength].CopyTo(firstKey);
            accountPath.Bytes[..StorageHashPrefixLength].CopyTo(lastKey);

            int removedEntry = 0;
            using (ISortedView storageReader = storageSnap.GetViewBetween(firstKey, lastKey))
            {
                IWriteOnlyKeyValueStore? storageWriter = storage;
                while (storageReader.MoveNext())
                {
                    storageWriter.Remove(storageReader.CurrentKey);
                    removedEntry++;
                }
            }

            return removedEntry;
        }

        public void RemoveAccount(in ValueHash256 addrHash)
        {
            ReadOnlySpan<byte> key = addrHash.Bytes[..StateKeyPrefixLength];
            state.Remove(key);
        }

        public void RemoveStorage(in ValueHash256 addrHash, in ValueHash256 slotHash)
        {
            ReadOnlySpan<byte> theKey = EncodeStorageKeyHashed(stackalloc byte[StorageKeyLength], addrHash, slotHash);
            storage.Remove(theKey);
        }

        public void SetStorage(in ValueHash256 addrHash, in ValueHash256 slotHash, ReadOnlySpan<byte> value)
        {
            ReadOnlySpan<byte> theKey = EncodeStorageKeyHashed(stackalloc byte[StorageKeyLength], addrHash, slotHash);
            storage.PutSpan(theKey, value, flags);
        }

        public void SetAccount(in ValueHash256 addrHash, ReadOnlySpan<byte> account)
        {
            ReadOnlySpan<byte> key = addrHash.Bytes[..StateKeyPrefixLength];
            state.PutSpan(key, account, flags);
        }
    }
}
