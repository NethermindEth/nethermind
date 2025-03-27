// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State.Tracing;
using Nethermind.State.Transition;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using Nethermind.Verkle.Tree.TreeStore;

namespace Nethermind.State;

public class VergeWorldStateProvider(
    ITrieStore merkleStateStore,
    IVerkleTreeStore verkleStateStore,
    StateReader merkleStateReader,
    ISpecProvider specProvider,
    IDbProvider dbProvider,
    ILogManager logManager)
    : IWorldState
{
    private IWorldState? _transitionWorldState = null;
    private IWorldState? _worldStateToUse = null;

    private VerkleWorldState _verkleState = new VerkleWorldState(verkleStateStore, dbProvider.CodeDb, logManager);
    private WorldState _merkleState = new WorldState(merkleStateStore, dbProvider.CodeDb, logManager);

    public bool StartGenesisBlockProcessing()
    {
        IReleaseSpec? genesisSpec = specProvider.GenesisSpec;

        if (genesisSpec.IsVerkleTreeEipEnabled)
        {
            _worldStateToUse = _verkleState;
        }
        else
        {
            _worldStateToUse = _merkleState;
        }

        if (currentStateRoot != Keccak.EmptyTreeHash) _worldStateToUse.StateRoot = currentStateRoot;
        return true;
    }

    public bool StartBlockProcessing(BlockHeader header)
    {
        IReleaseSpec currentSpec = specProvider.GetSpec(header);
        bool isTransitionBlock = false;
        if (header.Number != 0)
        {
            header.MaybeParent!.TryGetTarget(out BlockHeader parent);
            IReleaseSpec parentSpec = specProvider.GetSpec(parent!);
            isTransitionBlock = currentSpec.IsVerkleTreeEipEnabled && !parentSpec.IsVerkleTreeEipEnabled;
        }

        if (isTransitionBlock)
        {
            _transitionWorldState ??= new TransitionWorldState(merkleStateReader, _merkleState.StateRoot,
                new VerkleStateTree(verkleStateStore, logManager), dbProvider.CodeDb, dbProvider.Preimages, logManager);
            _worldStateToUse = _transitionWorldState;
        }
        else if (currentSpec.IsVerkleTreeEipEnabled)
        {
            _worldStateToUse = _verkleState;
        }
        else
        {
            _worldStateToUse = _merkleState;
        }

        if (currentStateRoot != Keccak.EmptyTreeHash) _worldStateToUse.StateRoot = currentStateRoot;
        return true;
    }

    public void ResetProvider()
    {
        currentStateRoot = _worldStateToUse?.StateRoot ?? Keccak.EmptyTreeHash;
        _worldStateToUse = null;
    }



    public void Restore(Snapshot snapshot)
    {
        if (_worldStateToUse is null) ProviderNotInitialized();
        _worldStateToUse.Restore(snapshot);
    }

    public bool TryGetAccount(Address address, out AccountStruct account)
    {
        if (_worldStateToUse is null) ProviderNotInitialized();
        return _worldStateToUse.TryGetAccount(address, out account);
    }

    public StateType StateType
    {
        get
        {
            if (_worldStateToUse is null) ProviderNotInitialized();
            return _worldStateToUse.StateType;
        }
    }

    public byte[] GetOriginal(in StorageCell storageCell)
    {
        if (_worldStateToUse is null) ProviderNotInitialized();
        return _worldStateToUse.GetOriginal(in storageCell);
    }

    public bool ValuePresentInTree(Hash256 key)
    {
        if (_worldStateToUse is null) ProviderNotInitialized();
        return _worldStateToUse.ValuePresentInTree(key);
    }

    public ReadOnlySpan<byte> Get(in StorageCell storageCell)
    {
        if (_worldStateToUse is null) ProviderNotInitialized();
        return _worldStateToUse.Get(in storageCell);
    }

    public void Set(in StorageCell storageCell, byte[] newValue)
    {
        if (_worldStateToUse is null) ProviderNotInitialized();
        _worldStateToUse.Set(in storageCell, newValue);
    }

    public ReadOnlySpan<byte> GetTransientState(in StorageCell storageCell)
    {
        if (_worldStateToUse is null) ProviderNotInitialized();
        return _worldStateToUse.GetTransientState(in storageCell);
    }

    public void SetTransientState(in StorageCell storageCell, byte[] newValue)
    {
        if (_worldStateToUse is null) ProviderNotInitialized();
        _worldStateToUse.SetTransientState(in storageCell, newValue);
    }

    public void Reset()
    {
        if (_worldStateToUse is null) ProviderNotInitialized();
        _worldStateToUse.Reset();
    }

    public Snapshot TakeSnapshot(bool newTransactionStart = false)
    {
        if (_worldStateToUse is null) ProviderNotInitialized();
        return _worldStateToUse.TakeSnapshot(newTransactionStart);
    }

    public void ClearStorage(Address address)
    {
        if (_worldStateToUse is null) ProviderNotInitialized();
        _worldStateToUse.ClearStorage(address);
    }

    public void RecalculateStateRoot()
    {
        if (_worldStateToUse is null) ProviderNotInitialized();
        _worldStateToUse.RecalculateStateRoot();
    }

    private Hash256 currentStateRoot = Keccak.EmptyTreeHash;

    public Hash256 StateRoot
    {
        get
        {
            if (_worldStateToUse is null) return currentStateRoot;
            return _worldStateToUse.StateRoot;
        }
        set
        {
            if (_worldStateToUse is null) currentStateRoot = value;
            else _worldStateToUse.StateRoot = value;
        }
    }

    public void DeleteAccount(Address address)
    {
        if (_worldStateToUse is null) ProviderNotInitialized();
        _worldStateToUse.DeleteAccount(address);
    }

    public void CreateAccount(Address address, in UInt256 balance, in UInt256 nonce = default)
    {
        if (_worldStateToUse is null) ProviderNotInitialized();
        _worldStateToUse.CreateAccount(address, in balance, in nonce);
    }

    public void CreateAccountIfNotExists(Address address, in UInt256 balance, in UInt256 nonce = default)
    {
        if (_worldStateToUse is null) ProviderNotInitialized();
        _worldStateToUse.CreateAccountIfNotExists(address, in balance, in nonce);
    }

    public void InsertCode(Address address, Hash256 codeHash, ReadOnlyMemory<byte> code, IReleaseSpec spec, bool isGenesis = false)
    {
        if (_worldStateToUse is null) ProviderNotInitialized();
        _worldStateToUse.InsertCode(address, codeHash, code, spec, isGenesis);
    }

    public void AddToBalance(Address address, in UInt256 balanceChange, IReleaseSpec spec)
    {
        if (_worldStateToUse is null) ProviderNotInitialized();
        _worldStateToUse.AddToBalance(address, in balanceChange, spec);
    }

    public void AddToBalanceAndCreateIfNotExists(Address address, in UInt256 balanceChange, IReleaseSpec spec)
    {
        if (_worldStateToUse is null) ProviderNotInitialized();
        _worldStateToUse.AddToBalanceAndCreateIfNotExists(address, in balanceChange, spec);
    }

    public void SubtractFromBalance(Address address, in UInt256 balanceChange, IReleaseSpec spec)
    {
        if (_worldStateToUse is null) ProviderNotInitialized();
        _worldStateToUse.SubtractFromBalance(address, in balanceChange, spec);
    }

    public void UpdateStorageRoot(Address address, Hash256 storageRoot)
    {
        if (_worldStateToUse is null) ProviderNotInitialized();
        _worldStateToUse.UpdateStorageRoot(address, storageRoot);
    }

    public void IncrementNonce(Address address)
    {
        if (_worldStateToUse is null) ProviderNotInitialized();
        _worldStateToUse.IncrementNonce(address);
    }

    public void DecrementNonce(Address address)
    {
        if (_worldStateToUse is null) ProviderNotInitialized();
        _worldStateToUse.DecrementNonce(address);
    }

    public void Commit(IReleaseSpec releaseSpec, bool isGenesis = false)
    {
        if (_worldStateToUse is null) ProviderNotInitialized();
        _worldStateToUse.Commit(releaseSpec, isGenesis);
    }

    public void Commit(IReleaseSpec releaseSpec, IWorldStateTracer? traver, bool isGenesis = false)
    {
        if (_worldStateToUse is null) ProviderNotInitialized();
        _worldStateToUse.Commit(releaseSpec, traver, isGenesis);
    }

    public void CommitTree(long blockNumber)
    {
        if (_worldStateToUse is null) ProviderNotInitialized();
        _worldStateToUse.CommitTree(blockNumber);
    }

    public void TouchCode(in ValueHash256 codeHash)
    {
        if (_worldStateToUse is null) ProviderNotInitialized();
        _worldStateToUse.TouchCode(in codeHash);
    }

    public byte[] GetCodeChunk(Address codeOwner, UInt256 chunkId)
    {
        if (_worldStateToUse is null) ProviderNotInitialized();
        return _worldStateToUse.GetCodeChunk(codeOwner, chunkId);
    }

    public byte[]? GetCode(Address address)
    {
        if (_worldStateToUse is null) ProviderNotInitialized();
        return _worldStateToUse.GetCode(address);
    }

    public byte[]? GetCode(Hash256 codeHash)
    {
        if (_worldStateToUse is null) ProviderNotInitialized();
        return _worldStateToUse.GetCode(codeHash);
    }

    public byte[]? GetCode(ValueHash256 codeHash)
    {
        if (_worldStateToUse is null) ProviderNotInitialized();
        return _worldStateToUse.GetCode(codeHash);
    }

    public bool IsContract(Address address)
    {
        if (_worldStateToUse is null) ProviderNotInitialized();
        return _worldStateToUse.IsContract(address);
    }

    public void Accept(ITreeVisitor visitor, Hash256 stateRoot, VisitingOptions? visitingOptions = null)
    {
        if (_worldStateToUse is null) ProviderNotInitialized();
        _worldStateToUse.Accept(visitor, stateRoot, visitingOptions);
    }

    public bool AccountExists(Address address)
    {
        if (_worldStateToUse is null) ProviderNotInitialized();
        return _worldStateToUse.AccountExists(address);
    }

    public bool IsDeadAccount(Address address)
    {
        if (_worldStateToUse is null) ProviderNotInitialized();
        return _worldStateToUse.IsDeadAccount(address);
    }

    public bool IsEmptyAccount(Address address)
    {
        if (_worldStateToUse is null) ProviderNotInitialized();
        return _worldStateToUse.IsEmptyAccount(address);
    }

    public bool HasStateForRoot(Hash256 stateRoot)
    {
        if (_worldStateToUse is null) ProviderNotInitialized();
        return _worldStateToUse.HasStateForRoot(stateRoot);
    }


    [DoesNotReturn]
    [StackTraceHidden]
    static void ProviderNotInitialized()
    {
        throw new InvalidOperationException("WorldState must be initialized before providing a state.");
    }

}
