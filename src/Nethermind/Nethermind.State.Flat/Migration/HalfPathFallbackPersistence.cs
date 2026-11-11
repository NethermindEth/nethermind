// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.State.Flat.Persistence;
using Nethermind.Trie;

namespace Nethermind.State.Flat.Migration;

/// <summary>Serves what an online HalfPath to flat migration has not copied yet from the HalfPath trie DB.</summary>
/// <remarks>
/// <para>
/// A read the <see cref="MigrationCursor"/> has passed is served by flat alone. Past it, a trie node flat misses is looked up in the
/// HalfPath DB by its path and hash, which is exact, and an account or slot is reported by
/// <see cref="IPersistence.IPersistenceReader.ShouldReadWithTrie(Address)"/>, so the caller reads it by walking the trie through
/// those node reads.
/// </para>
/// <para>
/// The walk must start from the caller's state root rather than the HalfPath one because flat deletes are physical: an account a
/// block deleted after the migration started has no flat entry, and must not be found again in the HalfPath trie.
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
        // Abandoned first: a chunk committed after the wipe would bring back data and a cursor the wipe deleted.
        cursor.Abandon();
        inner.Clear();
    }

    private sealed class Reader(IPersistence.IPersistenceReader inner, INodeStorage halfPath, MigrationCursor copied)
        : IPersistence.IPersistenceReader, IPersistence.IPrefetchingReader
    {
        public StateId CurrentState => inner.CurrentState;

        public bool IsPreimageMode => inner.IsPreimageMode;

        public Account? GetAccount(Address address) => inner.GetAccount(address);

        public bool TryGetSlot(Address address, in UInt256 slot, ref UInt256 outValue) => inner.TryGetSlot(address, slot, ref outValue);

        /// <inheritdoc/>
        /// <remarks>Counted in <see cref="Metrics.HalfPathFallbackWalks"/>, as the caller then walks the trie.</remarks>
        public bool ShouldReadWithTrie(Address address) => !copied.IsCopied(address.ToAccountPath) && CountWalk();

        /// <inheritdoc/>
        /// <remarks>Counted in <see cref="Metrics.HalfPathFallbackWalks"/>, as the caller then walks the trie.</remarks>
        public bool ShouldReadWithTrie(Address address, in UInt256 slot)
        {
            ValueHash256 slotPath = default;
            StorageTree.ComputeKeyWithLookup(slot, ref slotPath);
            return !copied.IsCopied(address.ToAccountPath, slotPath) && CountWalk();
        }

        private static bool CountWalk()
        {
            Metrics.IncrementHalfPathFallbackWalks();
            return true;
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
    }
}
