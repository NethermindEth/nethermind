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

    // No-op unless WAL is disabled: RocksDbPersistence flushes the WAL on write-batch dispose.
    void Flush();
    void Clear();

    public interface IPersistenceReader : IDisposable
    {
        Account? GetAccount(Address address);

        // Can return true with outValue set to zero: zero and missing are distinct (verkle compatibility).
        bool TryGetSlot(Address address, in UInt256 slot, ref SlotValue outValue);
        StateId CurrentState { get; }
        byte[]? TryLoadStateRlp(in TreePath path, ReadFlags flags);
        byte[]? TryLoadStorageRlp(Hash256 address, in TreePath path, ReadFlags flags);

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
        void DeleteStateTrieNodeRange(in TreePath fromPath, in TreePath toPath);
        void DeleteStorageTrieNodeRange(in ValueHash256 addressHash, in TreePath fromPath, in TreePath toPath);
    }

    public interface IFlatIterator : IDisposable
    {
        bool MoveNext();
        ValueHash256 CurrentKey { get; }
        ReadOnlySpan<byte> CurrentValue { get; }
    }
}
