// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using Nethermind.Core;
using Nethermind.Trie;

namespace Nethermind.State.StateStore;

public interface IReadWriteStateStore : IReadOnlyStateStore
{
    new byte[] StateRoot { get; set; }

    void SetStorageSlot(Address address, Account account);
    void SetStorageSlot(StorageCell storageCell, byte[] value);

    void SetCode(Address address, ReadOnlyMemory<byte> code);

    void SetBalance(Address address, byte[] balance);
    void SetNonce(Address address, byte[] nonce);
    void SetCodeHash(Address address, byte[] codeHash);

    void Commit(long blockNumber);
    void Dump(BufferedStream targetStream);
    TrieStats CollectStats();
    IStateStore MoveToStateRoot(byte[] stateRoot);
    byte[] GetStateRoot();
    void UpdateRootHash();

}

public interface IVerkleReadWriteStateStore : IReadWriteStateStore
{
    void SetVersion(Address address, byte[] version);
    void SetCodeChunk(CodeChunk codeChunk, byte[] code);
    void SetCodeSize(Address address, byte[] codeSize);

}

