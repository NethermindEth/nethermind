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
    IWriteBatch CreateWriteBatch(StateId from, StateId to);

    public interface IPersistenceReader: IDisposable
    {
        bool TryGetAccount(Address address, out Account? acc);
        bool TryGetSlot(Address address, in UInt256 index, out byte[] value);
        StateId CurrentState { get; }
        byte[]? TryLoadRlp(Hash256? address, in TreePath path, Hash256 hash, ReadFlags flags);

        byte[]? GetAccountRaw(Hash256? addrHash);
        byte[]? GetStorageRaw(Hash256? addrHash, Hash256 slotHash);
    }

    public interface IWriteBatch: IDisposable
    {
        int SelfDestruct(Address addr);
        void RemoveAccount(Address addr);
        void SetAccount(Address addr, Account account);
        void SetStorage(Address addr, UInt256 slot, ReadOnlySpan<byte> value);
        void SetTrieNodes(Hash256? address, TreePath path, TrieNode tnValue);
        void RemoveStorage(Address addr, UInt256 slot);

        void SetStorageRaw(Hash256? addrHash, Hash256 slotHash, ReadOnlySpan<byte> value);
        void SetAccountRaw(Hash256? addrHash, Account account);
        bool ConcurrentStorage => false;
    }

    bool SupportConcurrentWrites => true;
}
