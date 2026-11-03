// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.State;
using Nethermind.State.Flat.Persistence;
using Nethermind.Trie;

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
        public StateId CurrentState => inner.CurrentState;

        public bool IsPreimageMode => inner.IsPreimageMode;

        public Account? GetAccount(Address address)
        {
            Account? account = inner.GetAccount(address);
            if (account is not null) return account;

            ValueHash256 accountPath = address.ToAccountPath;
            if (copied.IsCopied(accountPath) || CurrentState.StateRoot == ValueKeccak.EmptyTreeHash) return null;

            Metrics.IncrementHalfPathFallbackWalks();
            ReadOnlySpan<byte> rlp = Tree(null).Get(accountPath.Bytes, CurrentState.StateRoot.ToCommitment());
            if (rlp.IsEmpty) return null;
            RlpReader reader = new(rlp);
            return AccountDecoder.Instance.Decode(ref reader);
        }

        public bool TryGetSlot(Address address, in UInt256 slot, ref UInt256 outValue)
        {
            if (inner.TryGetSlot(address, slot, ref outValue)) return true;

            ValueHash256 accountPath = address.ToAccountPath;
            ValueHash256 slotPath = default;
            StorageTree.ComputeKeyWithLookup(slot, ref slotPath);
            if (copied.IsCopied(accountPath, slotPath) || GetAccount(address) is not { HasStorage: true } account) return false;

            Metrics.IncrementHalfPathFallbackWalks();
            ReadOnlySpan<byte> rlp = Tree(accountPath.ToCommitment()).Get(slotPath.Bytes, account.StorageRoot);
            if (rlp.IsEmpty) return false;
            RlpReader reader = new(rlp);
            outValue = new UInt256(reader.DecodeByteArraySpan(), isBigEndian: true);
            return true;
        }

        private PatriciaTree Tree(Hash256? address) => new(new ReaderTrieStore(this, address), NullLogManager.Instance);

        public byte[]? TryLoadStateRlp(in TreePath path, in ValueHash256 hash, ReadFlags flags) =>
            inner.TryLoadStateRlp(path, hash, flags) ?? LoadUncopied(null, path, hash, flags);

        public byte[]? TryLoadStorageRlp(Hash256 address, in TreePath path, in ValueHash256 hash, ReadFlags flags) =>
            inner.TryLoadStorageRlp(address, path, hash, flags) ?? LoadUncopied(address, path, hash, flags);

        public bool TryLoadStateRlp<TArg>(in TreePath path, in ValueHash256 hash, ReadFlags flags, ReadOnlySpanAction<byte, TArg> action, TArg arg) where TArg : allows ref struct =>
            inner.TryLoadStateRlp(path, hash, flags, action, arg) || Pass(LoadUncopied(null, path, hash, flags), action, arg);

        public bool TryLoadStorageRlp<TArg>(Hash256 address, in TreePath path, in ValueHash256 hash, ReadFlags flags, ReadOnlySpanAction<byte, TArg> action, TArg arg) where TArg : allows ref struct =>
            inner.TryLoadStorageRlp(address, path, hash, flags, action, arg) || Pass(LoadUncopied(address, path, hash, flags), action, arg);

        private byte[]? LoadUncopied(Hash256? address, in TreePath path, in ValueHash256 hash, ReadFlags flags) =>
            copied.IsCopied(address, path) ? null : halfPath.Get(address, path, hash, flags);

        private static bool Pass<TArg>(byte[]? rlp, ReadOnlySpanAction<byte, TArg> action, TArg arg) where TArg : allows ref struct
        {
            if (rlp is null) return false;
            action(rlp, arg);
            return true;
        }

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
