// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing.State;
using Nethermind.Int256;

namespace Nethermind.State;

public class BlockAccessWorldState(BlockAccessList blockAccessList, ushort blockAccessIndex, IWorldState innerWorldState) : IWorldState
{
    public Hash256 StateRoot => throw new NotImplementedException();

    public bool AccountExists(Address address)
        => innerWorldState.AccountExists(address);

    public void AddToBalance(Address address, in UInt256 balanceChange, IReleaseSpec spec)
    {
        throw new NotImplementedException();
    }

    public bool AddToBalanceAndCreateIfNotExists(Address address, in UInt256 balanceChange, IReleaseSpec spec)
    {
        throw new NotImplementedException();
    }

    public void ClearStorage(Address address)
    {
        throw new NotImplementedException();
    }

    public void Commit(IReleaseSpec releaseSpec, bool isGenesis = false, bool commitRoots = true)
    {
        throw new NotImplementedException();
    }

    public void Commit(IReleaseSpec releaseSpec, IWorldStateTracer tracer, bool isGenesis = false, bool commitRoots = true)
    {
        throw new NotImplementedException();
    }

    public void CommitTree(long blockNumber)
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

    public void DecrementNonce(Address address, UInt256 delta)
    {
        throw new NotImplementedException();
    }

    public void DeleteAccount(Address address)
    {
        throw new NotImplementedException();
    }

    public ReadOnlySpan<byte> Get(in StorageCell storageCell)
    {
        throw new NotImplementedException();
    }

    public ArrayPoolList<AddressAsKey>? GetAccountChanges()
    {
        throw new NotImplementedException();
    }

    public ref readonly UInt256 GetBalance(Address address)
    {
        throw new NotImplementedException();
    }

    public byte[]? GetCode(Address address)
    {
        if (!blockAccessList.AccountChanges.TryGetValue(address, out AccountChanges accountChanges))
        {
            return innerWorldState.GetCode(address);
        }

        List<CodeChange> codeChanges = accountChanges.CodeChanges;
        CodeChange? lastChange = null;
        foreach (CodeChange codeChange in codeChanges)
        {
            if (codeChange.BlockAccessIndex >= blockAccessIndex)
            {
                break;
            }
            lastChange = codeChange;
        }

        if (lastChange is null)
        {
            return innerWorldState.GetCode(address);
        }

        return lastChange.Value.NewCode;
    }

    public byte[]? GetCode(in ValueHash256 codeHash)
    {
        throw new NotImplementedException();
    }

    public ref readonly ValueHash256 GetCodeHash(Address address)
    {
        throw new NotImplementedException();
    }

    public byte[] GetOriginal(in StorageCell storageCell)
    {
        throw new NotImplementedException();
    }

    public ReadOnlySpan<byte> GetTransientState(in StorageCell storageCell)
    {
        throw new NotImplementedException();
    }

    public bool HasStateForBlock(BlockHeader? baseBlock)
    {
        throw new NotImplementedException();
    }

    public void IncrementNonce(Address address, UInt256 delta)
    {
        throw new NotImplementedException();
    }

    public bool InsertCode(Address address, in ValueHash256 codeHash, ReadOnlyMemory<byte> code, IReleaseSpec spec, bool isGenesis = false)
    {
        throw new NotImplementedException();
    }

    public bool IsContract(Address address)
    {
        throw new NotImplementedException();
    }

    public bool IsDeadAccount(Address address)
    {
        throw new NotImplementedException();
    }

    public void RecalculateStateRoot()
    {
        throw new NotImplementedException();
    }

    public void Reset(bool resetBlockChanges = true)
    {
        throw new NotImplementedException();
    }

    public void ResetTransient()
    {
        throw new NotImplementedException();
    }

    public void Restore(Snapshot snapshot)
    {
        throw new NotImplementedException();
    }

    public void Set(in StorageCell storageCell, byte[] newValue)
    {
        throw new NotImplementedException();
    }

    public void SetBaseBlock(BlockHeader? header)
    {
        throw new NotImplementedException();
    }

    public void SetNonce(Address address, in UInt256 nonce)
    {
        throw new NotImplementedException();
    }

    public void SetTransientState(in StorageCell storageCell, byte[] newValue)
    {
        throw new NotImplementedException();
    }

    public void SubtractFromBalance(Address address, in UInt256 balanceChange, IReleaseSpec spec)
    {
        throw new NotImplementedException();
    }

    public Snapshot TakeSnapshot(bool newTransactionStart = false)
    {
        throw new NotImplementedException();
    }

    public bool TryGetAccount(Address address, out AccountStruct account)
    {
        throw new NotImplementedException();
    }

    public void UpdateStorageRoot(Address address, Hash256 storageRoot)
    {
        throw new NotImplementedException();
    }

    public void WarmUp(AccessList? accessList)
    {
        throw new NotImplementedException();
    }

    public void WarmUp(Address address)
    {
        throw new NotImplementedException();
    }
}