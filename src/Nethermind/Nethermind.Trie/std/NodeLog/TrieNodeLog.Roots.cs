// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Threading;
using Nethermind.Core.Crypto;

namespace Nethermind.Trie.NodeLog;

/// <remarks>
/// The root index is two tables of slots, one for the state trie and one for storage tries. A slot holds the pointer of the newest root key
/// record hashing to it, and each root key record links to the one it displaced, so colliding roots chain without locks or deletion.
/// A chain never links to a newer generation, so the first link that no longer resolves ends it.
/// Root key records live in the generations like node records and go away with them.
/// </remarks>
public sealed partial class TrieNodeLog
{
    private const int StateRootSlots = 1024;
    private const int MemoryPerStorageRootSlot = 4096;

    private const int RootKeyAddressOffset = 8;
    private const int RootKeyRootOffset = 40;
    private const int RootKeyPointerOffset = 72;
    private const int RootKeyPreviousOffset = 80;

    private readonly ulong[] _stateRoots = new ulong[StateRootSlots];
    private readonly ulong[] _storageRoots;

    /// <summary>The record pointer of the newest indexed <paramref name="root"/> of the trie, or 0 when it is not indexed or its record no longer resolves.</summary>
    /// <param name="address">The storage trie's address hash, or null for the state trie.</param>
    internal ulong FindRoot(Hash256? address, in ValueHash256 root)
    {
        ValueHash256 addressHash = address?.ValueHash256 ?? default;
        ulong pointer = Volatile.Read(ref RootSlot(address, root));
        while (TryLocate(pointer, out NodeRecordRef rootKey))
        {
            ReadOnlySpan<byte> record = rootKey.Span;
            if (MemoryMarshal.Read<ValueHash256>(record[RootKeyAddressOffset..]) == addressHash
                && MemoryMarshal.Read<ValueHash256>(record[RootKeyRootOffset..]) == root)
            {
                ulong rootPointer = BinaryPrimitives.ReadUInt64LittleEndian(record[RootKeyPointerOffset..]);
                if (!TryLocate(rootPointer, out _)) break;

                Metrics.IncrementTrieNodeLogRootHits();
                return rootPointer;
            }

            pointer = BinaryPrimitives.ReadUInt64LittleEndian(record[RootKeyPreviousOffset..]);
        }

        Metrics.IncrementTrieNodeLogRootMisses();
        return 0;
    }

    /// <summary>Indexes <paramref name="rootPointer"/> as the record of <paramref name="root"/>, ahead of any earlier entry of the same root.</summary>
    /// <param name="address">The storage trie's address hash, or null for the state trie.</param>
    internal void SetRoot(Hash256? address, in ValueHash256 root, ulong rootPointer)
    {
        ref ulong slot = ref RootSlot(address, root);
        ulong previous = Volatile.Read(ref slot);
        while (true)
        {
            ulong rootKey = Reserve(NodeRecord.RootKeyLength, out byte[] buffer, out int offset);
            Span<byte> record = buffer.AsSpan(offset, NodeRecord.RootKeyLength);
            record[0] = (byte)NodeRecordKind.RootKey;
            address?.Bytes.CopyTo(record[RootKeyAddressOffset..]);
            root.Bytes.CopyTo(record[RootKeyRootOffset..]);
            BinaryPrimitives.WriteUInt64LittleEndian(record[RootKeyPointerOffset..], rootPointer);
            BinaryPrimitives.WriteUInt64LittleEndian(record[RootKeyPreviousOffset..], previous);

            // A lost race appends a fresh record instead of relinking this one: the winner may sit in a newer generation.
            ulong current = Interlocked.CompareExchange(ref slot, rootKey, previous);
            if (current == previous) return;

            previous = current;
        }
    }

    private ref ulong RootSlot(Hash256? address, in ValueHash256 root)
    {
        ulong[] slots = address is null ? _stateRoots : _storageRoots;
        ulong addressWord = address is null ? 0 : MemoryMarshal.Read<ulong>(address.Bytes);
        return ref slots[(MemoryMarshal.Read<ulong>(root.Bytes) ^ addressWord) & (ulong)(slots.Length - 1)];
    }
}
