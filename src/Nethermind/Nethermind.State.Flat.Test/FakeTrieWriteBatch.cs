// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.State.Flat.Persistence;
using Nethermind.Trie;

namespace Nethermind.State.Flat.Test;

/// Wraps an inner <see cref="IPersistence.IWriteBatch"/> (typically an NSubstitute mock) and absorbs
/// the trie-node setters whose <see cref="System.ReadOnlySpan{T}"/> parameters Castle.DynamicProxy
/// cannot generate valid IL for. Trie-node calls are counted; everything else forwards.
internal sealed class FakeTrieWriteBatch(IPersistence.IWriteBatch inner) : IPersistence.IWriteBatch
{
    public int StateTrieNodeCalls { get; private set; }
    public int StorageTrieNodeCalls { get; private set; }

    public void SelfDestruct(Address addr) => inner.SelfDestruct(addr);
    public void SetAccount(Address addr, Account? account) => inner.SetAccount(addr, account);
    public void SetStorage(Address addr, in UInt256 slot, in SlotValue? value) => inner.SetStorage(addr, slot, value);
    public void SetStateTrieNode(in TreePath path, ReadOnlySpan<byte> rlp) => StateTrieNodeCalls++;
    public void SetStorageTrieNode(Hash256 address, in TreePath path, ReadOnlySpan<byte> rlp) => StorageTrieNodeCalls++;
    public void SetStorageRaw(in ValueHash256 addrHash, in ValueHash256 slotHash, in SlotValue? value) => inner.SetStorageRaw(addrHash, slotHash, value);
    public void SetAccountRaw(in ValueHash256 addrHash, Account account) => inner.SetAccountRaw(addrHash, account);
    public void DeleteAccountRange(in ValueHash256 fromPath, in ValueHash256 toPath) => inner.DeleteAccountRange(fromPath, toPath);
    public void DeleteStorageRange(in ValueHash256 addressHash, in ValueHash256 fromPath, in ValueHash256 toPath) => inner.DeleteStorageRange(addressHash, fromPath, toPath);
    public void DeleteStateTrieNodeRange(in TreePath fromPath, in TreePath toPath) => inner.DeleteStateTrieNodeRange(fromPath, toPath);
    public void DeleteStorageTrieNodeRange(in ValueHash256 addressHash, in TreePath fromPath, in TreePath toPath) => inner.DeleteStorageTrieNodeRange(addressHash, fromPath, toPath);

    public void Dispose() => inner.Dispose();
}
