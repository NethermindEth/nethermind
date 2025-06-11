// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.State;
using Nethermind.State.Tracing;
using Nethermind.Trie;

namespace Nethermind.Consensus.Stateless;

public class WitnessGeneratingWorldState(IWorldState inner) : IWorldState
{
    public Dictionary<Address, HashSet<UInt256>> StorageSlots { get; } = new();

    public byte[][] Keys => StorageSlots.Keys.Select(addr => addr.Bytes)
        .Concat(StorageSlots.Values.SelectMany(arr => arr.Select(slot => slot.ToBigEndian()))).ToArray();


    public void Restore(Snapshot snapshot)
    {
        inner.Restore(snapshot);
    }

    public bool TryGetAccount(Address address, out AccountStruct account)
    {
        StorageSlots.TryAdd(address, []);
        return inner.TryGetAccount(address, out account);
    }

    public Hash256 StateRoot { get => inner.StateRoot; set => inner.StateRoot = value; }

    public byte[]? GetCode(Address address)
    {
        StorageSlots.TryAdd(address, []);
        return inner.GetCode(address);
    }

    public byte[]? GetCode(in ValueHash256 codeHash)
    {
        return inner.GetCode(in codeHash);
    }

    public bool IsContract(Address address)
    {
        StorageSlots.TryAdd(address, []);
        return inner.IsContract(address);
    }

    public void Accept<TCtx>(ITreeVisitor<TCtx> visitor, Hash256 stateRoot, VisitingOptions? visitingOptions = null) where TCtx : struct, INodeContext<TCtx>
    {
        inner.Accept(visitor, stateRoot, visitingOptions);
    }

    public bool AccountExists(Address address)
    {
        StorageSlots.TryAdd(address, []);
        return inner.AccountExists(address);
    }

    public bool IsDeadAccount(Address address)
    {
        StorageSlots.TryAdd(address, []);
        return inner.IsDeadAccount(address);
    }

    public bool IsEmptyAccount(Address address)
    {
        StorageSlots.TryAdd(address, []);
        return inner.IsEmptyAccount(address);
    }

    public bool HasStateForRoot(Hash256 stateRoot)
    {
        return inner.HasStateForRoot(stateRoot);
    }

    public ref readonly UInt256 GetBalance(Address address)
    {
        StorageSlots.TryAdd(address, []);
        return ref inner.GetBalance(address);
    }

    public ref readonly ValueHash256 GetCodeHash(Address address)
    {
        StorageSlots.TryAdd(address, []);
        return ref inner.GetCodeHash(address);
    }

    public byte[] GetOriginal(in StorageCell storageCell)
    {
        if (StorageSlots.ContainsKey(storageCell.Address))
        {
            StorageSlots[storageCell.Address].Add(storageCell.Index);
        }
        else
        {
            StorageSlots.Add(storageCell.Address, new HashSet<UInt256>{storageCell.Index});
        }

        return inner.GetOriginal(in storageCell);
    }

    public ReadOnlySpan<byte> Get(in StorageCell storageCell)
    {
        if (StorageSlots.ContainsKey(storageCell.Address))
        {
            StorageSlots[storageCell.Address].Add(storageCell.Index);
        }
        else
        {
            StorageSlots.Add(storageCell.Address, new HashSet<UInt256>{storageCell.Index});
        }
        return inner.Get(in storageCell);
    }

    public void Set(in StorageCell storageCell, byte[] newValue)
    {
        if (StorageSlots.ContainsKey(storageCell.Address))
        {
            StorageSlots[storageCell.Address].Add(storageCell.Index);
        }
        else
        {
            StorageSlots.Add(storageCell.Address, new HashSet<UInt256>{storageCell.Index});
        }
        inner.Set(in storageCell, newValue);
    }

    public ReadOnlySpan<byte> GetTransientState(in StorageCell storageCell)
    {
        if (StorageSlots.ContainsKey(storageCell.Address))
        {
            StorageSlots[storageCell.Address].Add(storageCell.Index);
        }
        else
        {
            StorageSlots.Add(storageCell.Address, new HashSet<UInt256>{storageCell.Index});
        }
        return inner.GetTransientState(in storageCell);
    }

    public void SetTransientState(in StorageCell storageCell, byte[] newValue)
    {
        if (StorageSlots.ContainsKey(storageCell.Address))
        {
            StorageSlots[storageCell.Address].Add(storageCell.Index);
        }
        else
        {
            StorageSlots.Add(storageCell.Address, new HashSet<UInt256>{storageCell.Index});
        }
        inner.SetTransientState(in storageCell, newValue);
    }

    public void Reset(bool resetBlockChanges = true)
    {
        inner.Reset(resetBlockChanges);
    }

    public Snapshot TakeSnapshot(bool newTransactionStart = false)
    {
        return inner.TakeSnapshot(newTransactionStart);
    }

    public void WarmUp(AccessList? accessList)
    {
        inner.WarmUp(accessList);
    }

    public void WarmUp(Address address)
    {
        inner.WarmUp(address);
    }

    public void ClearStorage(Address address)
    {
        StorageSlots.TryAdd(address, []);
        inner.ClearStorage(address);
    }

    public void RecalculateStateRoot()
    {
        inner.RecalculateStateRoot();
    }

    public void DeleteAccount(Address address)
    {
        StorageSlots.TryAdd(address, []);
        inner.DeleteAccount(address);
    }

    public void CreateAccount(Address address, in UInt256 balance, in UInt256 nonce = default)
    {
        StorageSlots.TryAdd(address, []);
        inner.CreateAccount(address, in balance, in nonce);
    }

    public void CreateAccountIfNotExists(Address address, in UInt256 balance, in UInt256 nonce = default)
    {
        StorageSlots.TryAdd(address, []);
        inner.CreateAccountIfNotExists(address, in balance, in nonce);
    }

    public bool InsertCode(Address address, in ValueHash256 codeHash, ReadOnlyMemory<byte> code, IReleaseSpec spec,
        bool isGenesis = false)
    {
        StorageSlots.TryAdd(address, []);
        return inner.InsertCode(address, in codeHash, code, spec, isGenesis);
    }

    public void AddToBalance(Address address, in UInt256 balanceChange, IReleaseSpec spec)
    {
        StorageSlots.TryAdd(address, []);
        inner.AddToBalance(address, in balanceChange, spec);
    }

    public bool AddToBalanceAndCreateIfNotExists(Address address, in UInt256 balanceChange, IReleaseSpec spec)
    {
        StorageSlots.TryAdd(address, []);
        return inner.AddToBalanceAndCreateIfNotExists(address, in balanceChange, spec);
    }

    public void SubtractFromBalance(Address address, in UInt256 balanceChange, IReleaseSpec spec)
    {
        StorageSlots.TryAdd(address, []);
        inner.SubtractFromBalance(address, in balanceChange, spec);
    }

    public void UpdateStorageRoot(Address address, Hash256 storageRoot)
    {
        StorageSlots.TryAdd(address, []);
        inner.UpdateStorageRoot(address, storageRoot);
    }

    public void IncrementNonce(Address address, UInt256 delta)
    {
        StorageSlots.TryAdd(address, []);
        inner.IncrementNonce(address, delta);
    }

    public void DecrementNonce(Address address, UInt256 delta)
    {
        StorageSlots.TryAdd(address, []);
        inner.DecrementNonce(address, delta);
    }

    public void SetNonce(Address address, in UInt256 nonce)
    {
        StorageSlots.TryAdd(address, []);
        inner.SetNonce(address, nonce);
    }

    public void Commit(IReleaseSpec releaseSpec, bool isGenesis = false, bool commitRoots = true)
    {
        inner.Commit(releaseSpec, isGenesis, commitRoots);
    }

    public void Commit(IReleaseSpec releaseSpec, IWorldStateTracer? tracer, bool isGenesis = false, bool commitRoots = true)
    {
        inner.Commit(releaseSpec, tracer, isGenesis, commitRoots);
    }

    public void CommitTree(long blockNumber)
    {
        inner.CommitTree(blockNumber);
    }

    public ArrayPoolList<AddressAsKey>? GetAccountChanges()
    {
        return inner.GetAccountChanges();
    }

    public void ResetTransient()
    {
        inner.ResetTransient();
    }
}
