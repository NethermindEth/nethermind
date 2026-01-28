// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Trie;

namespace Nethermind.State.Flat.Persistence;

/// <summary>
/// Common persistence logic for Trie. The trie is encoded with 4 different database columns. This implementation
/// exploits the fact that a vast majority of the key's TreePath have a length less than 15, which can be encoded
/// with only 8 bytes. The average length of the TreePath only increases by 1 if the number of keys increases by 16x.
///
/// To handle cases where path length is greater than 15, a separate fallback column is used which stores both state
/// and storage nodes, with a prefix partition key (0x00 or 0x01) to separate them.
///
/// For storage, only 20 bytes of the hashed address are used. The first 4 bytes are placed in front while the
/// remaining 16 bytes are placed at the end. This makes RocksDB's index smaller due to index key shortening.
///
/// <code>
/// === Main Columns (optimized for short paths) ===
///
/// ┌─────────────────────────────────────────────────────────────────────────────────────────────────────────────┐
/// │ StateNodesTop (path length 0-5)                                                          Total: 3 bytes    │
/// ├──────────────┬──────────────┬──────────────────────────────────────────────────────────────────────────────┤
/// │ Byte 0       │ Byte 1       │ Byte 2                                                                       │
/// │ Path[0]      │ Path[1]      │ Path[2] upper 4 bits | Length lower 4 bits                                   │
/// └──────────────┴──────────────┴──────────────────────────────────────────────────────────────────────────────┘
///
/// ┌─────────────────────────────────────────────────────────────────────────────────────────────────────────────┐
/// │ StateNodes (path length 6-15)                                                            Total: 8 bytes    │
/// ├────────────────────────────────────────┬────────────────────────────────────────────────────────────────────┤
/// │ Bytes 0-6                              │ Byte 7                                                             │
/// │ Path[0..7]                             │ Path[7] upper 4 bits | Length lower 4 bits                         │
/// └────────────────────────────────────────┴────────────────────────────────────────────────────────────────────┘
///
/// ┌─────────────────────────────────────────────────────────────────────────────────────────────────────────────┐
/// │ StorageNodes (path length 0-15)                                                          Total: 28 bytes   │
/// ├──────────────────────────┬───────────────────────────────────┬──────────────────────────────────────────────┤
/// │ Bytes 0-3                │ Bytes 4-11                        │ Bytes 12-27                                  │
/// │ Address[0..4]            │ Path via EncodeWith8Byte          │ Address[4..20]                               │
/// └──────────────────────────┴───────────────────────────────────┴──────────────────────────────────────────────┘
///
/// === FallbackNodes Column (for long paths, prefix-partitioned) ===
///
/// ┌─────────────────────────────────────────────────────────────────────────────────────────────────────────────┐
/// │ State nodes (path length 16+)                                                            Total: 34 bytes   │
/// ├──────────────┬──────────────────────────────────────────────────────────────────┬───────────────────────────┤
/// │ Byte 0       │ Bytes 1-32                                                       │ Byte 33                   │
/// │ 0x00         │ Full 32-byte path                                                │ Path length               │
/// └──────────────┴──────────────────────────────────────────────────────────────────┴───────────────────────────┘
///
/// ┌─────────────────────────────────────────────────────────────────────────────────────────────────────────────┐
/// │ Storage nodes (path length 16+)                                                          Total: 54 bytes   │
/// ├────────┬────────────────┬─────────────────────────────────┬─────────────┬───────────────────────────────────┤
/// │ Byte 0 │ Bytes 1-4      │ Bytes 5-36                      │ Byte 37     │ Bytes 38-53                       │
/// │ 0x01   │ Address[0..4]  │ Full 32-byte path               │ Path length │ Address[4..20]                    │
/// └────────┴────────────────┴─────────────────────────────────┴─────────────┴───────────────────────────────────┘
/// </code>
/// </summary>
public static class BaseTriePersistence
{
    private const int StorageHashPrefixLength = 20; // Store prefix of the 32 byte of the storage. Reduces index size.
    private const int FullPathLength = 32;
    private const int PathLengthLength = 1;

    private const int ShortenedPathThreshold = 15; // Must be odd
    private const int ShortenedPathLength = 8; // ceil of ShortenedPathThreshold/2

    // Note to self: Splitting the storage tree have been shown to not improve block cache hit rate
    private const int StateNodesTopThreshold = 5;
    private const int StateNodesTopPathLength = 3;

    private const int FullStateNodesKeyLength = 1 + FullPathLength + PathLengthLength;

    private const int StoragePrefixPortion = BasePersistence.StoragePrefixPortion;
    private const int ShortenedStorageNodesKeyLength = StoragePrefixPortion + ShortenedPathLength + (StorageHashPrefixLength - StoragePrefixPortion);
    private const int FullStorageNodesKeyLength = 1 + StorageHashPrefixLength + FullPathLength + PathLengthLength;

    private static ReadOnlySpan<byte> EncodeStateTopNodeKey(Span<byte> buffer, in TreePath path)
    {
        // Looks like this <3-byte-path>
        // Last 4 bit of the path is the length

        path.Path.Bytes[0..StateNodesTopPathLength].CopyTo(buffer);
        // Pack length into lower 4 bits of last byte (upper 4 bits contain path data)
        byte lengthAsByte = (byte)path.Length;
        buffer[StateNodesTopPathLength - 1] = (byte)((buffer[StateNodesTopPathLength - 1] & 0xf0) | (lengthAsByte & 0x0f));
        return buffer[..StateNodesTopPathLength];
    }

    private static ReadOnlySpan<byte> EncodeShortenedStateNodeKey(Span<byte> buffer, in TreePath path)
    {
        // Looks like this <8-byte-path>
        // Last 4 bit of the path is the length

        path.EncodeWith8Byte(buffer);
        return buffer[..ShortenedPathLength];
    }

    private static ReadOnlySpan<byte> EncodeFullStateNodeKey(Span<byte> buffer, in TreePath path)
    {
        // Looks like this <0-constant><32-byte-path><1-byte-length>
        buffer[0] = 0;
        path.Path.Bytes.CopyTo(buffer[1..]);
        buffer[(1 + FullPathLength)] = (byte)path.Length;
        return buffer[..FullStateNodesKeyLength];
    }

    internal static ReadOnlySpan<byte> EncodeShortenedStorageNodeKey(Span<byte> buffer, Hash256 addr, in TreePath path)
    {
        // Looks like this <4-byte-address-prefix><8-byte-path-portion><16-byte-remaining-address>
        addr.Bytes[..StoragePrefixPortion].CopyTo(buffer);
        path.EncodeWith8Byte(buffer[StoragePrefixPortion..]);
        addr.Bytes[StoragePrefixPortion..StorageHashPrefixLength].CopyTo(buffer[(StoragePrefixPortion + ShortenedPathLength)..]);
        return buffer[..ShortenedStorageNodesKeyLength];
    }

    private static ReadOnlySpan<byte> EncodeFullStorageNodeKey(Span<byte> buffer, Hash256 address, in TreePath path)
    {
        // Looks like this <1-constant><4-byte-address-prefix><32-byte-path><1-byte-length><16-byte-remaining-address>
        buffer[0] = 1;
        address.Bytes[..StoragePrefixPortion].CopyTo(buffer[1..]);
        path.Path.Bytes.CopyTo(buffer[(1 + StoragePrefixPortion)..]);
        buffer[(1 + StoragePrefixPortion + FullPathLength)] = (byte)path.Length;
        address.Bytes[StoragePrefixPortion..StorageHashPrefixLength].CopyTo(buffer[(1 + StoragePrefixPortion + FullPathLength + PathLengthLength)..]);
        return buffer[..FullStorageNodesKeyLength];
    }

    public readonly struct WriteBatch(
        ISortedKeyValueStore storageNodesSnap,
        ISortedKeyValueStore fallbackNodesSnap,
        IWriteOnlyKeyValueStore stateTopNodes,
        IWriteOnlyKeyValueStore stateNodes,
        IWriteOnlyKeyValueStore storageNodes,
        IWriteOnlyKeyValueStore fallbackNodes,
        WriteFlags flags
    ) : BasePersistence.ITrieWriteBatch
    {

        [SkipLocalsInit]
        public void SelfDestruct(in ValueHash256 accountPath)
        {
            Span<byte> firstKeyAlloc = stackalloc byte[1 + StoragePrefixPortion];
            Span<byte> lastKeyAlloc = stackalloc byte[FullStorageNodesKeyLength + 1];

            // Technically, this is kinda not needed for nodes as it's always traversed so orphaned trie just get skipped.
            {
                Span<byte> firstKey = firstKeyAlloc[..StoragePrefixPortion];
                Span<byte> lastKey = lastKeyAlloc[..(ShortenedStorageNodesKeyLength + 1)];
                BasePersistence.CreateStorageRange(accountPath.Bytes, firstKey, lastKey);

                using ISortedView storageNodeReader = storageNodesSnap.GetViewBetween(firstKey, lastKey);
                while (storageNodeReader.MoveNext())
                {
                    // Double-check the end portion
                    if (Bytes.AreEqual(storageNodeReader.CurrentKey[(StoragePrefixPortion + ShortenedPathLength)..], accountPath.Bytes[StoragePrefixPortion..(StorageHashPrefixLength)]))
                    {
                        storageNodes.Remove(storageNodeReader.CurrentKey);
                    }
                }
            }

            {
                Span<byte> firstKey = firstKeyAlloc;
                Span<byte> lastKey = lastKeyAlloc;
                // Do the same for the fallback nodes, except that the key must be prefixed `1` also
                firstKey[0] = 1;
                lastKey[0] = 1;
                BasePersistence.CreateStorageRange(accountPath.Bytes, firstKey[1..], lastKey[1..]);
                using ISortedView storageNodeReader = fallbackNodesSnap.GetViewBetween(firstKey, lastKey);
                while (storageNodeReader.MoveNext())
                {
                    // Double-check the end portion
                    if (Bytes.AreEqual(storageNodeReader.CurrentKey[(1 + StoragePrefixPortion + FullPathLength + PathLengthLength)..], accountPath.Bytes[StoragePrefixPortion..(StorageHashPrefixLength)]))
                    {
                        fallbackNodes.Remove(storageNodeReader.CurrentKey);
                    }
                }
            }
        }

        public void SetStateTrieNode(in TreePath path, TrieNode tn)
        {
            switch (path.Length)
            {
                case <= StateNodesTopThreshold:
                    stateTopNodes.PutSpan(EncodeStateTopNodeKey(stackalloc byte[StateNodesTopPathLength], path), tn.FullRlp.Span, flags);
                    break;
                case <= ShortenedPathThreshold:
                    stateNodes.PutSpan(EncodeShortenedStateNodeKey(stackalloc byte[ShortenedPathLength], path), tn.FullRlp.Span, flags);
                    break;
                default:
                    fallbackNodes.PutSpan(EncodeFullStateNodeKey(stackalloc byte[FullStateNodesKeyLength], in path), tn.FullRlp.Span, flags);
                    break;
            }
        }

        public void SetStorageTrieNode(Hash256 address, in TreePath path, TrieNode tn)
        {
            switch (path.Length)
            {
                case <= ShortenedPathThreshold:
                    storageNodes.PutSpan(EncodeShortenedStorageNodeKey(stackalloc byte[ShortenedStorageNodesKeyLength], address, path), tn.FullRlp.Span, flags);
                    break;
                default:
                    fallbackNodes.PutSpan(EncodeFullStorageNodeKey(stackalloc byte[FullStorageNodesKeyLength], address, in path), tn.FullRlp.Span, flags);
                    break;
            }
        }
    }


    public readonly struct Reader(
        IReadOnlyKeyValueStore stateTopNodes,
        IReadOnlyKeyValueStore stateNodes,
        IReadOnlyKeyValueStore storageNodes,
        IReadOnlyKeyValueStore fallbackNodes
    ) : BasePersistence.ITrieReader
    {
        public byte[]? TryLoadStateRlp(in TreePath path, ReadFlags flags) =>
            path.Length switch
            {
                <= StateNodesTopThreshold => stateTopNodes.Get(EncodeStateTopNodeKey(stackalloc byte[StateNodesTopPathLength], in path), flags: flags),
                <= ShortenedPathThreshold => stateNodes.Get(EncodeShortenedStateNodeKey(stackalloc byte[ShortenedPathLength], in path), flags: flags),
                _ => fallbackNodes.Get(EncodeFullStateNodeKey(stackalloc byte[FullStateNodesKeyLength], in path), flags: flags)
            };

        public byte[]? TryLoadStorageRlp(Hash256 address, in TreePath path, ReadFlags flags) =>
            path.Length <= ShortenedPathThreshold
                ? storageNodes.Get(EncodeShortenedStorageNodeKey(stackalloc byte[ShortenedStorageNodesKeyLength], address, in path), flags: flags)
                : fallbackNodes.Get(EncodeFullStorageNodeKey(stackalloc byte[FullStorageNodesKeyLength], address, in path), flags: flags);
    }
}
