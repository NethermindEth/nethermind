using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.State.Tracing;
using Nethermind.Trie;

namespace Nethermind.State;

public class OverlayWorldState : IWorldState
{
    public void Restore(Snapshot snapshot)
    {
        throw new NotImplementedException();
    }

    public bool TryGetAccount(Address address, out AccountStruct account)
    {
        throw new NotImplementedException();
    }

    public Hash256 StateRoot { get; set; }

    public byte[]? GetCode(Address address)
    {
        throw new NotImplementedException();
    }

    public byte[]? GetCode(Hash256 codeHash)
    {
        throw new NotImplementedException();
    }

    public byte[]? GetCode(ValueHash256 codeHash)
    {
        throw new NotImplementedException();
    }

    public bool IsContract(Address address)
    {
        throw new NotImplementedException();
    }

    public void Accept(ITreeVisitor visitor, Hash256 stateRoot, VisitingOptions? visitingOptions = null)
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

    public bool HasStateForRoot(Hash256 stateRoot)
    {
        throw new NotImplementedException();
    }

    public StateType StateType { get; }
    public byte[] GetOriginal(in StorageCell storageCell)
    {
        throw new NotImplementedException();
    }

    public ReadOnlySpan<byte> Get(in StorageCell storageCell)
    {
        throw new NotImplementedException();
    }

    public void Set(in StorageCell storageCell, byte[] newValue)
    {
        throw new NotImplementedException();
    }

    public ReadOnlySpan<byte> GetTransientState(in StorageCell storageCell)
    {
        throw new NotImplementedException();
    }

    public void SetTransientState(in StorageCell storageCell, byte[] newValue)
    {
        throw new NotImplementedException();
    }

    public void Reset()
    {
        throw new NotImplementedException();
    }

    public Snapshot TakeSnapshot(bool newTransactionStart = false)
    {
        throw new NotImplementedException();
    }

    public void ClearStorage(Address address)
    {
        throw new NotImplementedException();
    }

    public void RecalculateStateRoot()
    {
        throw new NotImplementedException();
    }

    public void DeleteAccount(Address address)
    {
        throw new NotImplementedException();
    }

    public void CreateAccount(Address address, in UInt256 balance, in UInt256 nonce = default)
    {
        throw new NotImplementedException();
    }

    public void CreateAccountIfNotExists(Address address, in UInt256 balance, in UInt256 nonce = default)
    {
        throw new NotImplementedException();
    }

    public void InsertCode(Address address, Hash256 codeHash, ReadOnlyMemory<byte> code, IReleaseSpec spec, bool isGenesis = false)
    {
        throw new NotImplementedException();
    }

    public void AddToBalance(Address address, in UInt256 balanceChange, IReleaseSpec spec)
    {
        throw new NotImplementedException();
    }

    public void AddToBalanceAndCreateIfNotExists(Address address, in UInt256 balanceChange, IReleaseSpec spec)
    {
        throw new NotImplementedException();
    }

    public void SubtractFromBalance(Address address, in UInt256 balanceChange, IReleaseSpec spec)
    {
        throw new NotImplementedException();
    }

    public void UpdateStorageRoot(Address address, Hash256 storageRoot)
    {
        throw new NotImplementedException();
    }

    public void IncrementNonce(Address address)
    {
        throw new NotImplementedException();
    }

    public void DecrementNonce(Address address)
    {
        throw new NotImplementedException();
    }

    public void Commit(IReleaseSpec releaseSpec, bool isGenesis = false)
    {
        throw new NotImplementedException();
    }

    public void Commit(IReleaseSpec releaseSpec, IWorldStateTracer? traver, bool isGenesis = false)
    {
        throw new NotImplementedException();
    }

    public void CommitTree(long blockNumber)
    {
        throw new NotImplementedException();
    }

    public void TouchCode(in ValueHash256 codeHash)
    {
        throw new NotImplementedException();
    }

    public byte[] GetCodeChunk(Address codeOwner, UInt256 chunkId)
    {
        throw new NotImplementedException();
    }
}