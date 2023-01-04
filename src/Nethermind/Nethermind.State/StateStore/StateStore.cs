// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.State.StateStore;

public class StateStore : IStateStore
{
    public byte[] StateRoot
    {
        get
        {
            if (_needsStateRootUpdate)
            {
                throw new InvalidOperationException();
            }

            return _stateTree.RootHash.Bytes;
        }
        set => _stateTree.RootHash = new Keccak(value);
    }

    private readonly StateTree _stateTree;
    private readonly ITrieStore _trieStore;
    private readonly ILogger _logger;
    private readonly IKeyValueStore _codeDb;
    private bool _needsStateRootUpdate;

    public StateStore(ITrieStore trieStore, IKeyValueStore codeDb, ILogManager? logManager)
    {
        _trieStore = trieStore ?? throw new ArgumentNullException(nameof(trieStore));
        _stateTree = new StateTree(trieStore, logManager);
        _logger = logManager?.GetClassLogger<StateStore>() ?? throw new ArgumentNullException(nameof(logManager));
        _codeDb = codeDb ?? throw new ArgumentNullException(nameof(codeDb));
    }

    public Account? Get(Address address)
    {
        return _stateTree.Get(address);
    }

    public void Set(Address address, Account account)
    {
        _needsStateRootUpdate = true;
        _stateTree.Set(address, account);
    }

    public void Set(StorageCell storageCell, byte[] value)
    {
        _needsStateRootUpdate = true;
    }

    public byte[] Get(StorageCell storageCell)
    {
        throw new System.NotImplementedException();
    }

    public void Set(CodeChunk codeChunk, byte[] value)
    {
        _needsStateRootUpdate = true;
    }

    public Keccak UpdateCode(ReadOnlyMemory<byte> code)
    {
        _needsStateRootUpdate = true;
        if (code.Length == 0)
        {
            return Keccak.OfAnEmptyString;
        }

        Keccak codeHash = Keccak.Compute(code.Span);

        _codeDb[codeHash.Bytes] = code.ToArray();

        return codeHash;
    }

    public byte[]? GetCode(Keccak codeHash)
    {
        return codeHash == Keccak.OfAnEmptyString ? Array.Empty<byte>() : _codeDb[codeHash.Bytes];
    }

    public byte[] GetStorageRoot(Address address)
    {
        throw new NotImplementedException();
    }
    public bool IsContract(Address address)
    {
        throw new NotImplementedException();
    }
    public void Accept(ITreeVisitor visitor, byte[] stateRoot, VisitingOptions? visitingOptions = null)
    {
        if (visitor is null) throw new ArgumentNullException(nameof(visitor));
        if (stateRoot is null) throw new ArgumentNullException(nameof(stateRoot));

        _stateTree.Accept(visitor, new Keccak(stateRoot), visitingOptions);
    }

    public void SetStorageSlot(Address address, Account account)
    {
        throw new NotImplementedException();
    }
    public void SetStorageSlot(StorageCell storageCell, byte[] value)
    {
        throw new NotImplementedException();
    }
    public void SetCode(Address address, ReadOnlyMemory<byte> code)
    {
        _needsStateRootUpdate = true;
        Keccak codeHash = Keccak.Compute(code.Span);
        _codeDb[codeHash.Bytes] = code.ToArray();
        SetCodeHash(address, codeHash.Bytes);
    }

    public byte[] Get(CodeChunk codeChunk)
    {
        throw new System.NotImplementedException();
    }

    public byte[]? GetCode(Address address)
    {
        return GetCode(new Keccak(GetCodeHash(address)));
    }

    public byte[] GetVersion(Address address)
    {
        throw new System.NotImplementedException();
    }

    public void SetVersion(Address address, byte[] version)
    {
        throw new NotImplementedException();
    }

    public byte[] GetBalance(Address address)
    {
        throw new System.NotImplementedException();
    }

    public void SetBalance(Address address, byte[] balance)
    {
        throw new NotImplementedException();
    }

    public byte[] GetNonce(Address address)
    {
        throw new System.NotImplementedException();
    }

    public void SetNonce(Address address, byte[] nonce)
    {
        throw new NotImplementedException();
    }

    public byte[] GetCodeHash(Address address)
    {
        throw new NotImplementedException();
    }

    public void SetCodeHash(Address address, byte[] codeHash)
    {
        throw new NotImplementedException();
    }

    public byte[] GetCodeKeccak(Address address)
    {
        throw new System.NotImplementedException();
    }

    public byte[] GetCodeSize(Address address)
    {
        throw new System.NotImplementedException();
    }

    public void SetCodeSize(Address address, byte[] codeSize)
    {
        throw new NotImplementedException();
    }

    public byte[] GetCodeChunk(Address address, UInt256 chunkId)
    {
        throw new System.NotImplementedException();
    }

    public byte[] GetStorageSlot(StorageCell storageCell)
    {
        throw new NotImplementedException();
    }
    public byte[] GetStorageSlot(Address address, UInt256 storageSlot)
    {
        throw new System.NotImplementedException();
    }

    public void Commit(long blockNumber)
    {
        throw new System.NotImplementedException();
    }

    public void Dump(BufferedStream targetStream)
    {
        throw new System.NotImplementedException();
    }

    public TrieStats CollectStats()
    {
        throw new System.NotImplementedException();
    }
    public IStateStore MoveToStateRoot(byte[] stateRoot)
    {
        throw new NotImplementedException();
    }

    public IStateStore MoveToStateRoot(Keccak stateRoot)
    {
        return this;
    }

    public byte[] GetStateRoot() => StateRoot;

    public void UpdateRootHash()
    {
        _stateTree.UpdateRootHash();
        _needsStateRootUpdate = false;
    }

    public void Accept(ITreeVisitor visitor, Keccak rootHash, VisitingOptions? visitingOptions = null)
    {
        throw new NotImplementedException();
    }

    public bool AccountExists(Address address)
    {
        throw new NotImplementedException();
    }

    public bool IsDeadAccount(Address address)
    {
        throw new NotImplementedException();
    }

    public bool IsEmptyAccount(Address address)
    {
        throw new NotImplementedException();
    }

    public Account GetAccount(Address address)
    {
        throw new NotImplementedException();
    }
}
