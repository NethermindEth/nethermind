// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.State.Flat.Rust.Interop;
using Nethermind.Trie;
using static Nethermind.State.Flat.Rust.Interop.OpFieldWriter;

namespace Nethermind.State.Flat.Rust;

/// <summary>A trie node handed to the native manager, which the <c>IsPersisted</c> replay marks once it is persisted.</summary>
/// <param name="Address">The account path of a storage trie node; null for a state trie node.</param>
internal readonly record struct ShellNode(Hash256? Address, TreePath Path, TrieNode Node);

/// <summary>
/// Serializes the content of a <see cref="Snapshot"/> into a native snapshot builder, as the records of
/// native/crates/flatdb-ffi/src/snapshot_stream.rs, and collects the trie nodes it hands over.
/// </summary>
/// <remarks>
/// A large snapshot is serialized on three threads, each into a builder of its own that the first one absorbs: the
/// flat writes, the state trie nodes and the storage trie nodes are disjoint, so the order they are absorbed in does
/// not matter. Hash-only placeholders are handed over with empty RLP and are not collected, as no persist writes them.
/// </remarks>
internal static unsafe class SnapshotSerializer
{
    private const byte SetAccountRecord = 0;
    private const byte DeleteAccountRecord = 1;
    private const byte SetSlotRecord = 2;
    private const byte DeleteSlotRecord = 3;
    private const byte SelfDestructRecord = 4;
    private const byte StateNodeRecord = 5;
    private const byte StorageNodeRecord = 6;

    private const int BufferSize = 64 * 1024;

    /// <summary>Entries below which a snapshot is serialized on the calling thread alone.</summary>
    private const int ParallelThreshold = 4096;

    private static readonly byte[] ZeroHash = new byte[HashSize];

    /// <summary>Builds the native snapshot of <paramref name="snapshot"/>, returning the builder and the trie nodes it holds.</summary>
    /// <param name="nodes">The trie nodes handed over, which the caller disposes.</param>
    public static nint Build(Snapshot snapshot, out ArrayPoolList<ShellNode> nodes)
    {
        StateId from = snapshot.From;
        StateId to = snapshot.To;
        int accounts = snapshot.AccountsCount;
        int storages = snapshot.StoragesCount;
        int stateNodes = snapshot.StateNodesCount;
        int storageNodes = snapshot.StorageNodesCount;
        bool parallel = accounts + storages + stateNodes + storageNodes >= ParallelThreshold;

        ArrayPoolList<ShellNode> collected = new(stateNodes + storageNodes);
        Part? flat = null;
        Part? stateNodePart = null;
        Part? storageNodePart = null;
        try
        {
            if (parallel)
            {
                flat = new Part(from, to, accounts, storages, 0, 0);
                stateNodePart = new Part(from, to, 0, 0, stateNodes, 0);
                storageNodePart = new Part(from, to, 0, 0, 0, storageNodes);
                using ArrayPoolList<ShellNode> collectedStorageNodes = new(storageNodes);
                Parallel.Invoke(
                    () => flat.WriteFlat(snapshot),
                    () => stateNodePart.WriteStateNodes(snapshot, collected),
                    () => storageNodePart.WriteStorageNodes(snapshot, collectedStorageNodes));
                flat.Absorb(stateNodePart);
                flat.Absorb(storageNodePart);
                collected.AddRange(collectedStorageNodes.AsSpan());
            }
            else
            {
                flat = new Part(from, to, accounts, storages, stateNodes, storageNodes);
                flat.WriteFlat(snapshot);
                flat.WriteStateNodes(snapshot, collected);
                flat.WriteStorageNodes(snapshot, collected);
            }

            nint builder = flat.TakeBuilder();
            nodes = collected;
            return builder;
        }
        catch
        {
            collected.Dispose();
            throw;
        }
        finally
        {
            flat?.Dispose();
            stateNodePart?.Dispose();
            storageNodePart?.Dispose();
        }
    }

    /// <summary>One builder with the buffer its records are written to.</summary>
    private sealed class Part : IDisposable
    {
        private byte[] _buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        private int _length;
        private nint _builder;

        /// <summary>A builder with room for the writes of each kind it is about to be handed.</summary>
        public Part(in StateId from, in StateId to, int accounts, int storages, int stateNodes, int storageNodes)
        {
            FfiStateId nativeFrom = new(from);
            FfiStateId nativeTo = new(to);
            nint builder;
            StatusMap.ThrowIfFailed(NativeMethods.fdb_bld_new(&nativeFrom, &nativeTo, (nuint)accounts, (nuint)storages, (nuint)stateNodes, (nuint)storageNodes, &builder));
            _builder = builder;
        }

        public void WriteFlat(Snapshot snapshot)
        {
            foreach (KeyValuePair<HashedKey<Address>, bool> selfDestruct in snapshot.SelfDestructedStorageAddresses)
            {
                OpFieldWriter record = Append(SelfDestructRecord, AddressSize + 1);
                record.Write(selfDestruct.Key.Key.Bytes);
                record.Write(selfDestruct.Value ? (byte)1 : (byte)0);
            }

            foreach (KeyValuePair<HashedKey<Address>, Account?> account in snapshot.Accounts)
            {
                if (account.Value is null)
                {
                    Append(DeleteAccountRecord, AddressSize).Write(account.Key.Key.Bytes);
                    continue;
                }

                OpFieldWriter record = Append(SetAccountRecord, AddressSize + AccountSize);
                record.Write(account.Key.Key.Bytes);
                record.Write(account.Value);
            }

            foreach (KeyValuePair<HashedKey<(Address, UInt256)>, UInt256?> slot in snapshot.Storages)
            {
                (Address address, UInt256 index) = slot.Key.Key;
                if (slot.Value is not { } value)
                {
                    OpFieldWriter delete = Append(DeleteSlotRecord, AddressSize + UInt256Size);
                    delete.Write(address.Bytes);
                    delete.Write(index);
                    continue;
                }

                OpFieldWriter record = Append(SetSlotRecord, AddressSize + 2 * UInt256Size);
                record.Write(address.Bytes);
                record.Write(index);
                record.Write(value);
            }
        }

        public void WriteStateNodes(Snapshot snapshot, ArrayPoolList<ShellNode> nodes)
        {
            foreach (KeyValuePair<HashedKey<TreePath>, TrieNode> entry in snapshot.StateNodes)
            {
                TreePath path = entry.Key.Key;
                TrieNode node = entry.Value;
                ReadOnlySpan<byte> rlp = RlpOf(node);
                OpFieldWriter record = Append(StateNodeRecord, PathSize + HashSize + LengthSize + rlp.Length);
                record.Write(path);
                record.Write(HashOf(node));
                record.WriteWithLength(rlp);
                if (!rlp.IsEmpty) nodes.Add(new ShellNode(null, path, node));
            }
        }

        public void WriteStorageNodes(Snapshot snapshot, ArrayPoolList<ShellNode> nodes)
        {
            foreach (KeyValuePair<HashedKey<(Hash256, TreePath)>, TrieNode> entry in snapshot.StorageNodes)
            {
                (Hash256 address, TreePath path) = entry.Key.Key;
                TrieNode node = entry.Value;
                ReadOnlySpan<byte> rlp = RlpOf(node);
                OpFieldWriter record = Append(StorageNodeRecord, HashSize + PathSize + HashSize + LengthSize + rlp.Length);
                record.Write(address.Bytes);
                record.Write(path);
                record.Write(HashOf(node));
                record.WriteWithLength(rlp);
                if (!rlp.IsEmpty) nodes.Add(new ShellNode(address, path, node));
            }
        }

        /// <summary>Hands over what is buffered and moves the records of <paramref name="other"/> into this builder.</summary>
        public void Absorb(Part other)
        {
            HandOver();
            other.HandOver();
            // The native call consumes the builder whatever it returns.
            nint absorbed = other._builder;
            other._builder = 0;
            StatusMap.ThrowIfFailed(NativeMethods.fdb_bld_absorb(_builder, absorbed));
        }

        /// <summary>Hands over what is buffered and gives up the builder to the caller.</summary>
        public nint TakeBuilder()
        {
            HandOver();
            nint builder = _builder;
            _builder = 0;
            return builder;
        }

        public void Dispose()
        {
            if (_buffer.Length == 0) return;

            if (_builder != 0) NativeMethods.fdb_bld_release(_builder);
            _builder = 0;
            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = [];
        }

        /// <summary>Hashes are not computed for inline nodes, which are then handed over with a zero hash.</summary>
        private static ReadOnlySpan<byte> HashOf(TrieNode node) => node.Keccak is { } keccak ? keccak.Bytes : ZeroHash;

        private static ReadOnlySpan<byte> RlpOf(TrieNode node) =>
            node.IsHashOnlyPlaceholder() ? ReadOnlySpan<byte>.Empty : node.FullRlp.AsSpan();

        /// <summary>Reserves room for a record of <paramref name="size"/> bytes after its tag, handing over the buffer first if it is full.</summary>
        private OpFieldWriter Append(byte tag, int size)
        {
            int needed = 1 + size;
            if (_length + needed > _buffer.Length)
            {
                HandOver();
                if (needed > _buffer.Length)
                {
                    ArrayPool<byte>.Shared.Return(_buffer);
                    _buffer = ArrayPool<byte>.Shared.Rent(needed);
                }
            }

            Span<byte> record = _buffer.AsSpan(_length, needed);
            _length += needed;
            record[0] = tag;
            return new OpFieldWriter(record[1..]);
        }

        private void HandOver()
        {
            if (_length == 0) return;

            fixed (byte* records = _buffer)
            {
                StatusMap.ThrowIfFailed(NativeMethods.fdb_bld_append(_builder, records, (nuint)_length));
            }

            _length = 0;
        }
    }
}
