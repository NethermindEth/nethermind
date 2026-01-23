// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Trie;

namespace Nethermind.State.Flat.Persistence;

public interface IPersistence
{
    IPersistenceReader CreateReader();
    IWriteBatch CreateWriteBatch(StateId from, StateId to, WriteFlags flags = WriteFlags.None);

    /// <summary>
    /// Iterator for iterating over flat storage key-value pairs.
    /// </summary>
    public interface IFlatIterator : IDisposable
    {
        bool MoveNext();
        ValueHash256 CurrentKey { get; }
        ReadOnlySpan<byte> CurrentValue { get; }
    }

    public interface IPersistenceReader : IDisposable
    {
        Account? GetAccount(Address address);
        bool TryGetSlot(Address address, in UInt256 slot, ref SlotValue outValue);
        StateId CurrentState { get; }
        byte[]? TryLoadStateRlp(in TreePath path, ReadFlags flags);
        byte[]? TryLoadStorageRlp(Hash256 address, in TreePath path, ReadFlags flags);

        byte[]? GetAccountRaw(Hash256 addrHash);
        byte[]? GetStorageRaw(Hash256 addrHash, Hash256 slotHash);

        /// <summary>
        /// Creates an iterator over all accounts in flat storage.
        /// </summary>
        IFlatIterator CreateAccountIterator();

        /// <summary>
        /// Creates an iterator over all storage slots for a given account.
        /// </summary>
        IFlatIterator CreateStorageIterator(in ValueHash256 accountKey);

        /// <summary>
        /// Indicates whether the persistence uses preimage mode (raw addresses/slots instead of hashes).
        /// </summary>
        bool IsPreimageMode { get; }
    }

    public interface IWriteBatch : IDisposable
    {
        int SelfDestruct(Address addr);
        void SetAccount(Address addr, Account? account);
        void SetStorage(Address addr, in UInt256 slot, in SlotValue? value);
        void SetStateTrieNode(in TreePath path, TrieNode tnValue);
        void SetStorageTrieNode(Hash256 address, in TreePath path, TrieNode tnValue);

        void SetStorageRaw(Hash256 addrHash, Hash256 slotHash, in SlotValue? value);
        void SetAccountRaw(Hash256 addrHash, Account account);
    }
}
