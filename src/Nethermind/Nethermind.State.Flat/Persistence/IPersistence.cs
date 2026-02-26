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
    IWriteBatch CreateWriteBatch(in StateId from, in StateId to, WriteFlags flags = WriteFlags.None);

    // Note: RocksdbPersistence already flush WAL on writing batch dispose. You don't need this unless you are skipping WAL.
    void Flush();

    public interface IPersistenceReader : IDisposable
    {
        Account? GetAccount(Address address);

        // Note: It can return true while setting outValue to zero. This is because there is a distinction between
        // zero and missing to conform to a potential verkle need.
        bool TryGetSlot(Address address, in UInt256 slot, ref SlotValue outValue);
        StateId CurrentState { get; }
        byte[]? TryLoadStateRlp(in TreePath path, ReadFlags flags);
        byte[]? TryLoadStorageRlp(Hash256 address, in TreePath path, ReadFlags flags);

        // Raw operations are used in importer
        byte[]? GetAccountRaw(Hash256 addrHash);
        bool TryGetStorageRaw(Hash256 addrHash, Hash256 slotHash, ref SlotValue value);

        IFlatIterator CreateAccountIterator();
        IFlatIterator CreateStorageIterator(in ValueHash256 accountKey);
        bool IsPreimageMode { get; }
    }

    public interface IWriteBatch : IDisposable
    {
        void SelfDestruct(Address addr);
        void SetAccount(Address addr, Account? account);
        void SetStorage(Address addr, in UInt256 slot, in SlotValue? value);
        void SetStateTrieNode(in TreePath path, TrieNode tnValue);
        void SetStorageTrieNode(Hash256 address, in TreePath path, TrieNode tnValue);

        void SetStorageRaw(Hash256 addrHash, Hash256 slotHash, in SlotValue? value);
        void SetAccountRaw(Hash256 addrHash, Account account);

        void DeleteAccountRange(in ValueHash256 fromPath, in ValueHash256 toPath);
        void DeleteStorageRange(in ValueHash256 addressHash, in ValueHash256 fromPath, in ValueHash256 toPath);
        void DeleteStateTrieNodeRange(in TreePath fromPath, in TreePath toPath);
        void DeleteStorageTrieNodeRange(in ValueHash256 addressHash, in TreePath fromPath, in TreePath toPath);
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
