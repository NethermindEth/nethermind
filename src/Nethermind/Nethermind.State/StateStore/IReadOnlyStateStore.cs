// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Trie;

namespace Nethermind.State.StateStore;

public interface IReadOnlyStateStore
{
    byte[] StateRoot { get; }

    [Obsolete("No need to update and fetch full accounts", false)]
    Account GetAccount(Address address);

    byte[] GetStorageSlot(StorageCell storageCell);
    byte[] GetStorageSlot(Address address, UInt256 storageSlot);



    byte[] GetBalance(Address address);
    byte[] GetNonce(Address address);
    byte[] GetCodeHash(Address address);


    byte[] GetCode(Address address);


    // TODO: do we need this? - do not think so
    byte[] GetCode(Keccak codeHash);

    byte[] GetStorageRoot(Address address);

    public bool IsContract(Address address);

    /// <summary>
    /// Runs a visitor over trie.
    /// </summary>
    /// <param name="visitor">Visitor to run.</param>
    /// <param name="stateRoot">Root to run on.</param>
    /// <param name="visitingOptions">Options to run visitor.</param>
    void Accept(ITreeVisitor visitor, byte[] stateRoot, VisitingOptions? visitingOptions = null);

    bool AccountExists(Address address);

    bool IsDeadAccount(Address address);

    bool IsEmptyAccount(Address address);

}

public interface IVerkleReadOnlyStateStore : IReadOnlyStateStore
{
    byte[] GetVersion(Address address);

    byte[] GetCodeChunk(CodeChunk codeChunk);

    byte[] GetCodeChunk(Address address, UInt256 chunkId);

    byte[] GetCodeSize(Address address);
}
