// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Flat.Persistence;
using Nethermind.Trie;
using Nethermind.Trie.NodeBuffer;

namespace Nethermind.State.Flat.Migration;

/// <summary>Serves what an online HalfPath to flat migration has not copied yet from the HalfPath trie DB.</summary>
/// <remarks>
/// <para>
/// A read the <see cref="MigrationCursor"/> has passed is served by flat alone. Past it, a flat miss falls back: a trie node is
/// looked up in the HalfPath DB by its path and hash, which is exact, and an account or slot is read by walking the trie from the
/// reader's own state root through those node reads.
/// </para>
/// <para>
/// The walk starts from flat's root rather than the HalfPath one because flat deletes are physical: an account a block deleted
/// after the migration started has no flat entry, and must not be found again in the HalfPath trie.
/// </para>
/// </remarks>
public sealed class HalfPathFallbackPersistence(IPersistence inner, INodeStorage halfPath, HalfPathMigrationCursor cursor) : IPersistence
{
    /// <inheritdoc/>
    public IPersistence.IPersistenceReader CreateReader(ReaderFlags flags = ReaderFlags.None)
    {
        // Read before the inner snapshot is taken, so the cursor never covers data that snapshot lacks.
        MigrationCursor? copied = cursor.Current;
        IPersistence.IPersistenceReader reader = inner.CreateReader(flags);
        return copied is { } position ? new Reader(reader, halfPath, position) : reader;
    }

    /// <inheritdoc/>
    public IPersistence.IWriteBatch CreateWriteBatch(in StateId from, in StateId to, WriteFlags flags = WriteFlags.None) =>
        inner.CreateWriteBatch(from, to, flags);

    /// <inheritdoc/>
    public void Flush() => inner.Flush();

    /// <inheritdoc/>
    /// <remarks>The wipe deletes the cursor too, so the migration is over.</remarks>
    public void Clear()
    {
        inner.Clear();
        cursor.Abandon();
    }

    private sealed class Reader(IPersistence.IPersistenceReader inner, INodeStorage halfPath, MigrationCursor copied)
        : IPersistence.IPersistenceReader, IPersistence.IPrefetchingReader
    {
        private static readonly ReadOnlySpanAction<byte, Span<TrieLookup>> Descend = static (node, walk) => walk[0].Descend(node);

        public StateId CurrentState => inner.CurrentState;

        public bool IsPreimageMode => inner.IsPreimageMode;

        public Account? GetAccount(Address address)
        {
            Account? account = inner.GetAccount(address);
            if (account is not null) return account;

            ValueHash256 accountPath = address.ToAccountPath;
            if (copied.IsCopied(accountPath) || CurrentState.StateRoot == ValueKeccak.EmptyTreeHash) return null;

            Metrics.IncrementHalfPathFallbackWalks();
            TrieLookup lookup = new(accountPath, isStorage: false);
            Walk(null, CurrentState.StateRoot, ref lookup);
            return lookup.Account;
        }

        public bool TryGetSlot(Address address, in UInt256 slot, ref UInt256 outValue)
        {
            if (inner.TryGetSlot(address, slot, ref outValue)) return true;

            ValueHash256 accountPath = address.ToAccountPath;
            ValueHash256 slotPath = default;
            StorageTree.ComputeKeyWithLookup(slot, ref slotPath);
            if (copied.IsCopied(accountPath, slotPath) || GetAccount(address) is not { HasStorage: true } account) return false;

            Metrics.IncrementHalfPathFallbackWalks();
            TrieLookup lookup = new(slotPath, isStorage: true);
            Walk(accountPath, account.StorageRoot.ValueHash256, ref lookup);
            if (!lookup.Found) return false;
            outValue = lookup.Slot;
            return true;
        }

        /// <summary>Walks the trie from <paramref name="root"/> to the key of <paramref name="lookup"/>, reading each node through this reader.</summary>
        /// <param name="address">The account path of the storage trie, or <c>null</c> for the state trie.</param>
        /// <exception cref="MissingTrieNodeException">A node on the way is in neither flat nor the HalfPath DB.</exception>
        private void Walk(in ValueHash256? address, in ValueHash256 root, ref TrieLookup lookup)
        {
            lookup.NodeHash = root;
            Span<TrieLookup> walk = new(ref lookup);
            while (!lookup.IsDone)
            {
                // Copied, as the node read advances them while the loads still hold them by reference.
                TreePath path = lookup.NodePath;
                ValueHash256 hash = lookup.NodeHash;
                bool loaded = address is { } storage
                    ? TryLoadStorageRlp(storage, path, hash, ReadFlags.None, Descend, walk)
                    : TryLoadStateRlp(path, hash, ReadFlags.None, Descend, walk);
                if (!loaded)
                {
                    throw new MissingTrieNodeException($"Node A:{address} P:{path} H:{hash} is missing from both flat and the HalfPath DB",
                        address is { } missingAddress ? new Hash256(missingAddress) : null, path, new Hash256(hash));
                }
            }
        }

        public byte[]? TryLoadStateRlp(in TreePath path, in ValueHash256 hash, ReadFlags flags) =>
            inner.TryLoadStateRlp(path, hash, flags) ?? LoadUncopied(null, path, hash, flags);

        public byte[]? TryLoadStorageRlp(Hash256 address, in TreePath path, in ValueHash256 hash, ReadFlags flags) =>
            inner.TryLoadStorageRlp(address, path, hash, flags) ?? LoadUncopied(address, path, hash, flags);

        public bool TryLoadStateRlp<TArg>(in TreePath path, in ValueHash256 hash, ReadFlags flags, ReadOnlySpanAction<byte, TArg> action, TArg arg) where TArg : allows ref struct =>
            inner.TryLoadStateRlp(path, hash, flags, action, arg) || (!copied.IsCopied(null, path) && halfPath.TryGet(null, path, hash, flags, action, arg));

        public bool TryLoadStorageRlp<TArg>(in ValueHash256 address, in TreePath path, in ValueHash256 hash, ReadFlags flags, ReadOnlySpanAction<byte, TArg> action, TArg arg) where TArg : allows ref struct =>
            inner.TryLoadStorageRlp(address, path, hash, flags, action, arg) || (!copied.IsCopied(address, path) && halfPath.TryGet(address, path, hash, flags, action, arg));

        private byte[]? LoadUncopied(Hash256? address, in TreePath path, in ValueHash256 hash, ReadFlags flags) =>
            copied.IsCopied(address, path) ? null : halfPath.Get(address, path, hash, flags);

        public byte[]? TryLoadStateRlp(in TreePath path, ReadFlags flags) => inner.TryLoadStateRlp(path, flags);

        public byte[]? TryLoadStorageRlp(Hash256 address, in TreePath path, ReadFlags flags) => inner.TryLoadStorageRlp(address, path, flags);

        public byte[]? GetAccountRaw(in ValueHash256 addrHash) => inner.GetAccountRaw(addrHash);

        public bool TryGetStorageRaw(in ValueHash256 addrHash, in ValueHash256 slotHash, ref UInt256 value) => inner.TryGetStorageRaw(addrHash, slotHash, ref value);

        public IPersistence.IFlatIterator CreateAccountIterator(in ValueHash256 startKey, in ValueHash256 endKey) => inner.CreateAccountIterator(startKey, endKey);

        public IPersistence.IFlatIterator CreateStorageIterator(in ValueHash256 accountKey, in ValueHash256 startSlotKey, in ValueHash256 endSlotKey) =>
            inner.CreateStorageIterator(accountKey, startSlotKey, endSlotKey);

        public bool SupportsPrefetch => inner is IPersistence.IPrefetchingReader { SupportsPrefetch: true };

        public IPersistence.PrefetchColumns PlanPrefetch(Span<Address> accountSamples, Span<StorageCell> slotSamples) =>
            inner is IPersistence.IPrefetchingReader prefetcher ? prefetcher.PlanPrefetch(accountSamples, slotSamples) : IPersistence.PrefetchColumns.None;

        public void Prefetch(IPersistence.PrefetchColumns columns, Span<Address> accounts, Span<StorageCell> slots, CancellationToken cancellationToken)
        {
            if (inner is IPersistence.IPrefetchingReader prefetcher) prefetcher.Prefetch(columns, accounts, slots, cancellationToken);
        }

        public void Dispose() => inner.Dispose();

        /// <summary>The walk to one key of a trie, advanced by the borrowed RLP of each node read on the way.</summary>
        /// <param name="isStorage">Whether the key is a slot, whose value is decoded to <see cref="Slot"/>, rather than an account.</param>
        private struct TrieLookup(in ValueHash256 key, bool isStorage)
        {
            private const int KeyNibbles = 64;
            private readonly TreePath _key = new(key, KeyNibbles);

            /// <summary>The path of the next node to read, until <see cref="IsDone"/>.</summary>
            public TreePath NodePath;
            /// <summary>The hash of the next node to read, until <see cref="IsDone"/>.</summary>
            public ValueHash256 NodeHash;
            public bool IsDone;
            public bool Found;
            public Account? Account;
            public UInt256 Slot;

            /// <summary>Follows the key through <paramref name="node"/> and the nodes embedded in it, to the next node to read or the end of the walk.</summary>
            public void Descend(ReadOnlySpan<byte> node)
            {
                Span<byte> nibbles = stackalloc byte[KeyNibbles];
                while (true)
                {
                    RlpReader reader = new(node);
                    reader.ReadSequenceLength();
                    if (reader.PeekNumberOfItemsRemaining(maxSearch: 3) > 2)
                    {
                        int nibble = _key[NodePath.Length];
                        reader.SkipItems(nibble);
                        NodePath.AppendMut(nibble);
                    }
                    else
                    {
                        ReadOnlySpan<byte> hexPrefix = reader.DecodeByteArraySpan();
                        int length = NodeRlp.HexPrefixNibbleCount(hexPrefix);
                        if (length > KeyNibbles - NodePath.Length)
                        {
                            IsDone = true;
                            return;
                        }

                        NodeRlp.ReadHexPrefix(hexPrefix, 0, nibbles);
                        TreePath childPath = NodePath.Append(nibbles[..length]);
                        if (NodeRlp.IsLeafHexPrefix(hexPrefix))
                        {
                            if (childPath == _key) Decode(reader.DecodeByteArraySpan());
                            IsDone = true;
                            return;
                        }

                        if (!_key.StartsWith(childPath))
                        {
                            IsDone = true;
                            return;
                        }

                        NodePath = childPath;
                    }

                    if (reader.IsSequenceNext())
                    {
                        node = reader.PeekNextItem();
                        continue;
                    }

                    ReadOnlySpan<byte> childHash = reader.DecodeByteArraySpan();
                    if (childHash.IsEmpty) IsDone = true;
                    else NodeHash = new ValueHash256(childHash);
                    return;
                }
            }

            private void Decode(ReadOnlySpan<byte> value)
            {
                Found = true;
                RlpReader reader = new(value);
                if (isStorage) Slot = new UInt256(reader.DecodeByteArraySpan(), isBigEndian: true);
                else Account = AccountDecoder.Instance.Decode(ref reader);
            }
        }
    }
}
