// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Trie;

namespace Nethermind.State.Flat.Persistence;

[Flags]
public enum ReaderFlags
{
    None = 0,
    Sync = 1,
}

public interface IPersistence
{
    IPersistenceReader CreateReader(ReaderFlags flags = ReaderFlags.None);
    IWriteBatch CreateWriteBatch(in StateId from, in StateId to, WriteFlags flags = WriteFlags.None);

    // Note: RocksdbPersistence already flush WAL on writing batch dispose. You don't need this unless you are skipping WAL.
    void Flush();
    void Clear();

    public interface IPersistenceReader : IDisposable
    {
        Account? GetAccount(Address address);

        void GetAccounts(ReadOnlySpan<Address> addresses, Span<Account?> accounts)
        {
            if (addresses.Length != accounts.Length)
                throw new ArgumentException("Addresses and accounts must have the same length.", nameof(accounts));

            for (int i = 0; i < addresses.Length; i++)
                accounts[i] = GetAccount(addresses[i]);
        }

        // Note: It can return true while setting outValue to zero. This is because there is a distinction between
        // zero and missing to conform to a potential verkle need.
        bool TryGetSlot(Address address, in UInt256 slot, ref SlotValue outValue);

        void GetSlots(ReadOnlySpan<StorageCell> storageCells, Span<SlotValue> slots, Span<bool> found)
        {
            if (storageCells.Length != slots.Length || storageCells.Length != found.Length)
                throw new ArgumentException("Storage cells, slots, and found flags must have the same length.", nameof(slots));

            for (int i = 0; i < storageCells.Length; i++)
            {
                StorageCell cell = storageCells[i];
                found[i] = TryGetSlot(cell.Address, cell.Index, ref slots[i]);
            }
        }

        StateId CurrentState { get; }
        byte[]? TryLoadStateRlp(in TreePath path, ReadFlags flags);
        byte[]? TryLoadStorageRlp(Hash256 address, in TreePath path, ReadFlags flags);

        // Raw operations are used in importer
        byte[]? GetAccountRaw(in ValueHash256 addrHash);
        bool TryGetStorageRaw(in ValueHash256 addrHash, in ValueHash256 slotHash, ref SlotValue value);

        IFlatIterator CreateAccountIterator(in ValueHash256 startKey, in ValueHash256 endKey);
        IFlatIterator CreateStorageIterator(in ValueHash256 accountKey, in ValueHash256 startSlotKey, in ValueHash256 endSlotKey);
        bool IsPreimageMode { get; }
    }

    public interface IWriteBatch : IDisposable
    {
        void SelfDestruct(Address addr);
        void SetAccount(Address addr, Account? account);
        void SetStorage(Address addr, in UInt256 slot, in SlotValue? value);
        void SetStateTrieNode(in TreePath path, scoped ReadOnlySpan<byte> rlp);
        void SetStorageTrieNode(Hash256 address, in TreePath path, scoped ReadOnlySpan<byte> rlp);

        /// <summary>
        /// Writes a slot whose value is already the trie-leaf RLP byte string (<c>RLP(stripped)</c>), as produced
        /// during sync. When slot values are RLP-wrapped the bytes are stored verbatim; in raw mode the value is
        /// unwrapped to its stripped bytes.
        /// </summary>
        void SetStorageRawEncoded(in ValueHash256 addrHash, in ValueHash256 slotHash, scoped ReadOnlySpan<byte> rlpValue);
        void SetAccountRaw(in ValueHash256 addrHash, Account account);

        void DeleteAccountRange(in ValueHash256 fromPath, in ValueHash256 toPath);
        void DeleteStorageRange(in ValueHash256 addressHash, in ValueHash256 fromPath, in ValueHash256 toPath);

        /// <summary>
        /// Deletes every state trie node whose node and subtree are entirely contained within the value range
        /// <c>[<paramref name="from"/>, <paramref name="to"/>]</c> — i.e. every node at path P for which
        /// <c><paramref name="from"/> &lt;= P.ToLowerBoundPath()</c> and <c>P.ToUpperBoundPath() &lt;= <paramref name="to"/></c>.
        /// A node whose subtree only partially overlaps the range (an ancestor of the range) is left intact.
        /// </summary>
        void DeleteStateTrieNodeRange(in ValueHash256 from, in ValueHash256 to);

        /// <inheritdoc cref="DeleteStateTrieNodeRange"/>
        /// <remarks>Restricted to the storage trie of the account identified by <paramref name="addressHash"/>.</remarks>
        void DeleteStorageTrieNodeRange(in ValueHash256 addressHash, in ValueHash256 from, in ValueHash256 to);
    }

    /// <summary>
    /// Iterator for iterating over flat storage key-value pairs. This is mainly used in verifytrie.
    /// </summary>
    public interface IFlatIterator : IDisposable
    {
        bool MoveNext();
        ValueHash256 CurrentKey { get; }
        ReadOnlySpan<byte> CurrentValue { get; }
    }
}
