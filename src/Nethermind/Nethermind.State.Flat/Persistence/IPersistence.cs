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

    bool WarmUpWhole(CancellationToken cancellation) => true;

    public interface IPersistenceReader: IDisposable
    {
        Account? GetAccount(Address address);
        bool TryGetSlot(Address address, in UInt256 slot, ref SlotValue outValue);
        StateId CurrentState { get; }
        byte[]? TryLoadStateRlp(in TreePath path, ReadFlags flags);
        byte[]? TryLoadStorageRlp(Hash256 address, in TreePath path, ReadFlags flags);

        byte[]? GetAccountRaw(Hash256 addrHash);
        byte[]? GetStorageRaw(Hash256 addrHash, Hash256 slotHash);
    }

    public interface IWriteBatch: IDisposable
    {
        int SelfDestruct(Address addr);
        void SetAccount(Address addr, Account? account);
        void SetStorage(Address addr, in UInt256 slot, in SlotValue? value);
        void SetStateTrieNode(in TreePath path, TrieNode tnValue);
        void SetStorageTrieNode(Hash256 address, in TreePath path, TrieNode tnValue);

        void SetStorageRaw(Hash256 addrHash, Hash256 slotHash, in SlotValue? value);
        void SetAccountRaw(Hash256 addrHash, Account account);
    }

    bool SupportConcurrentWrites => true;
}

/// <summary>
/// Implementing this makes import fasteer
/// </summary>
public interface IPersistenceWithConcurrentTrie
{
    IWriteBatch CreateTrieWriteBatch(WriteFlags flags = WriteFlags.None);

    public interface IWriteBatch: IDisposable
    {
        void SetStateTrieNode(in TreePath path, TrieNode tnValue);
        void SetStorageTrieNode(Hash256 address, in TreePath path, TrieNode tnValue);
    }
}
