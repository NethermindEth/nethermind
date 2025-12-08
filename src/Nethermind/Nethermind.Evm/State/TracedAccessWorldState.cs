// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm.Tracing.State;
using Nethermind.Int256;

namespace Nethermind.Evm.State;

//rename
public class TracedAccessWorldState(IWorldState innerWorldState, bool enableParallelExecution) : WrappedWorldState(innerWorldState), IPreBlockCaches
{
    public bool TracingEnabled { get; set; } = false;
    public bool ParallelExecutionEnabled => TracingEnabled && enableParallelExecution;

    public BlockAccessList GeneratedBlockAccessList = new();
    private BlockAccessList _suggestedBlockAccessList;
    private BlockAccessList[] _intermediateBlockAccessLists;


    public PreBlockCaches Caches => (_innerWorldState as IPreBlockCaches).Caches;

    public bool IsWarmWorldState => (_innerWorldState as IPreBlockCaches).IsWarmWorldState;

    public void SetupGeneratedAccessLists(int txCount)
        => _intermediateBlockAccessLists = new BlockAccessList[txCount + 1];

    public void LoadSuggestedBlockAccessList(BlockAccessList suggested)
    {
        LoadPreBlockState(suggested);
        _suggestedBlockAccessList = suggested;
    }

    private void LoadPreBlockState(BlockAccessList blockAccessList)
    {
        foreach (AccountChanges accountChanges in blockAccessList.AccountChanges)
        {
            _innerWorldState.TryGetAccount(accountChanges.Address, out AccountStruct account);
            accountChanges.AddBalanceChange(new(-1, account.Balance));
            accountChanges.AddNonceChange(new(-1, (ulong)account.Nonce));
            accountChanges.CodeHash = account.CodeHash;

            foreach (SlotChanges slotChanges in accountChanges.StorageChanges)
            {
                StorageCell storageCell = new(accountChanges.Address, new(slotChanges.Slot.AsSpan()));
                slotChanges.AddStorageChange(new(-1, [.. _innerWorldState.Get(storageCell)]));
            }

            foreach (StorageRead storageRead in accountChanges.StorageReads)
            {
                SlotChanges slotChanges = accountChanges.GetOrAddSlotChanges(storageRead.Key);
                StorageCell storageCell = new(accountChanges.Address, new(storageRead.Key.AsSpan()));
                slotChanges.AddStorageChange(new(-1, [.. _innerWorldState.Get(storageCell)]));
            }
        }
    }

    public void ApplyStateChanges(IReleaseSpec spec, bool shouldComputeStateRoot)
    {
        foreach (AccountChanges accountChanges in _suggestedBlockAccessList.AccountChanges)
        {
            if (accountChanges.BalanceChanges.Count > 0)
            {
                _innerWorldState.AddToBalance(accountChanges.Address, _innerWorldState.GetBalance(accountChanges.Address) - accountChanges.BalanceChanges.Last().PostBalance, spec);
            }
        }
        _innerWorldState.Commit(spec);
        if (shouldComputeStateRoot)
        {
            _innerWorldState.RecalculateStateRoot();
        }
    }

    public void GenerateBlockAccessList()
    {
        // combine intermidiate BALs and receipt tracers
    }

    public override void AddToBalance(Address address, in UInt256 balanceChange, IReleaseSpec spec)
        => AddToBalance(address, balanceChange, spec, out _);

    public override void AddToBalance(Address address, in UInt256 balanceChange, IReleaseSpec spec, out UInt256 oldBalance)
    {
        _innerWorldState.AddToBalance(address, balanceChange, spec, out oldBalance);

        if (TracingEnabled)
        {
            UInt256 newBalance = oldBalance + balanceChange;
            GeneratedBlockAccessList.AddBalanceChange(address, oldBalance, newBalance);
        }
    }

    public override bool AddToBalanceAndCreateIfNotExists(Address address, in UInt256 balanceChange, IReleaseSpec spec)
        => AddToBalanceAndCreateIfNotExists(address, balanceChange, spec, out _);

    public override bool AddToBalanceAndCreateIfNotExists(Address address, in UInt256 balanceChange, IReleaseSpec spec, out UInt256 oldBalance)
    {
        bool res = _innerWorldState.AddToBalanceAndCreateIfNotExists(address, balanceChange, spec, out oldBalance);

        if (TracingEnabled)
        {
            UInt256 newBalance = oldBalance + balanceChange;
            GeneratedBlockAccessList.AddBalanceChange(address, oldBalance, newBalance);
        }

        return res;
    }

    public override IDisposable BeginScope(BlockHeader? baseBlock)
    {
        // GeneratedBlockAccessList = new();
        return _innerWorldState.BeginScope(baseBlock);
    }

    public override ReadOnlySpan<byte> Get(in StorageCell storageCell)
    {
        if (TracingEnabled)
        {
            GeneratedBlockAccessList.AddStorageRead(storageCell);
        }
        return _innerWorldState.Get(storageCell);
    }

    public override void IncrementNonce(Address address, UInt256 delta)
        => IncrementNonce(address, delta, out _);

    public override void IncrementNonce(Address address, UInt256 delta, out UInt256 oldNonce)
    {
        _innerWorldState.IncrementNonce(address, delta, out oldNonce);

        if (TracingEnabled)
        {
            GeneratedBlockAccessList.AddNonceChange(address, (ulong)(oldNonce + delta));
        }
    }

    public override void SetNonce(Address address, in UInt256 nonce)
    {
        _innerWorldState.SetNonce(address, nonce);

        if (TracingEnabled)
        {
            GeneratedBlockAccessList.AddNonceChange(address, (ulong)nonce);
        }
    }

    public override bool InsertCode(Address address, in ValueHash256 codeHash, ReadOnlyMemory<byte> code, IReleaseSpec spec, bool isGenesis = false)
    {
        if (TracingEnabled)
        {
            byte[] oldCode = _innerWorldState.GetCode(address) ?? [];
            GeneratedBlockAccessList.AddCodeChange(address, oldCode, code.ToArray());
        }
        return _innerWorldState.InsertCode(address, codeHash, code, spec, isGenesis);
    }

    public override void Set(in StorageCell storageCell, byte[] newValue)
    {
        if (TracingEnabled)
        {
            ReadOnlySpan<byte> oldValue = _innerWorldState.Get(storageCell);
            GeneratedBlockAccessList.AddStorageChange(storageCell, [.. oldValue], newValue);
        }
        if (!ParallelExecutionEnabled)
        {
            _innerWorldState.Set(storageCell, newValue);
        }
    }

    public override UInt256 GetBalance(Address address, int? blockAccessIndex = null)
    {
        if (!blockAccessIndex.HasValue)
            throw new ArgumentNullException(nameof(blockAccessIndex));

        AddAccountRead(address, blockAccessIndex);

        if (ParallelExecutionEnabled)
        {
            // get from BAL -> suggested block -> inner world state
            AccountChanges? accountChanges = GetGeneratingBlockAccessList().GetAccountChanges(address);
            if (accountChanges is not null && accountChanges.BalanceChanges.Count == 1)
            {
                return accountChanges.BalanceChanges.First().PostBalance;
            }

            accountChanges = _suggestedBlockAccessList.GetAccountChanges(address);

            if (accountChanges is not null && accountChanges.BalanceChanges.Count > 0)
            {
                return accountChanges.GetBalance(blockAccessIndex.Value);
            }

            return _innerWorldState.GetBalance(address);
            
        }
        else
        {
            return _innerWorldState.GetBalance(address);
        }
    }

    public override ValueHash256 GetCodeHash(Address address)
    {
        AddAccountRead(address);
        return _innerWorldState.GetCodeHash(address);
    }

    public override byte[]? GetCode(Address address)
    {
        AddAccountRead(address);
        return _innerWorldState.GetCode(address);
    }

    public override void SubtractFromBalance(Address address, in UInt256 balanceChange, IReleaseSpec spec)
    {
        if (TracingEnabled)
        {
            UInt256 before = _innerWorldState.GetBalance(address);
            UInt256 after = before - balanceChange;
            GeneratedBlockAccessList.AddBalanceChange(address, before, after);
        }
        if (!ParallelExecutionEnabled)
        {
            _innerWorldState.SubtractFromBalance(address, balanceChange, spec);
        }
    }

    public override void DeleteAccount(Address address)
    {
        if (TracingEnabled)
        {
            GeneratedBlockAccessList.DeleteAccount(address, _innerWorldState.GetBalance(address));
        }
        if (!ParallelExecutionEnabled)
        {
            _innerWorldState.DeleteAccount(address);
        }
    }

    public override void CreateAccount(Address address, in UInt256 balance, in UInt256 nonce = default)
    {
        if (TracingEnabled)
        {
            GeneratedBlockAccessList.AddAccountRead(address);
            // move inside bal
            if (balance != 0)
            {
                GeneratedBlockAccessList.AddBalanceChange(address, 0, balance);
            }
            if (nonce != 0)
            {
                GeneratedBlockAccessList.AddNonceChange(address, (ulong)nonce);
            }
        }
        if (!ParallelExecutionEnabled)
        {
            _innerWorldState.CreateAccount(address, balance, nonce);
        }
    }

    public override void CreateAccountIfNotExists(Address address, in UInt256 balance, in UInt256 nonce = default)
    {
        if (!ParallelExecutionEnabled && !_innerWorldState.AccountExists(address))
        {
            CreateAccount(address, balance, nonce);
        }
    }

    // maybe should remove?
    public override bool TryGetAccount(Address address, out AccountStruct account)
    {
        AddAccountRead(address);
        return _innerWorldState.TryGetAccount(address, out account);
    }

    public void AddAccountRead(Address address, int? blockAccessIndex = null)
    {
        if (TracingEnabled)
        {
            GetGeneratingBlockAccessList(blockAccessIndex).AddAccountRead(address);
        }
    }

    public override void Restore(Snapshot snapshot)
    {
        if (TracingEnabled)
        {
            GeneratedBlockAccessList.Restore(snapshot.BlockAccessListSnapshot);
        }
        if (!ParallelExecutionEnabled)
        {
            _innerWorldState.Restore(snapshot);
        }
    }

    public override Snapshot TakeSnapshot(bool newTransactionStart = false)
    {
        int blockAccessListSnapshot = GeneratedBlockAccessList.TakeSnapshot();
        Snapshot snapshot = ParallelExecutionEnabled ? Snapshot.Empty : _innerWorldState.TakeSnapshot(newTransactionStart);
        return new(snapshot.StorageSnapshot, snapshot.StateSnapshot, blockAccessListSnapshot);
    }

    public override bool AccountExists(Address address)
    {
        AddAccountRead(address);
        return _innerWorldState.AccountExists(address);
    }

    public override bool IsContract(Address address)
    {
        AddAccountRead(address);
        return _innerWorldState.IsContract(address);
    }

    public override bool IsDeadAccount(Address address)
    {
        AddAccountRead(address);
        return _innerWorldState.IsDeadAccount(address);
    }

    public override void ClearStorage(Address address)
    {
        AddAccountRead(address);
        if (!ParallelExecutionEnabled)
        {
            _innerWorldState.ClearStorage(address);
        }
    }

    public override void Commit(IReleaseSpec releaseSpec, bool isGenesis = false, bool commitRoots = true)
    {
        if (!ParallelExecutionEnabled)
        {
            _innerWorldState.Commit(releaseSpec, isGenesis, commitRoots);
        }
    }

    public override void Commit(IReleaseSpec releaseSpec, IWorldStateTracer tracer, bool isGenesis = false, bool commitRoots = true)
    {
        if (!ParallelExecutionEnabled)
        {
            _innerWorldState.Commit(releaseSpec, isGenesis, commitRoots);
        }
    }

    public override void RecalculateStateRoot()
    {
        if (!ParallelExecutionEnabled)
        {
            _innerWorldState.RecalculateStateRoot();
        }
    }

    // needed? also trygetaccount?
    // public override bool IsStorageEmpty(Address address)
    // {
    //     return false;
    // }

    private BlockAccessList GetGeneratingBlockAccessList(int? blockAccessIndex = null)
        => ParallelExecutionEnabled ? _intermediateBlockAccessLists[blockAccessIndex!.Value] : GeneratedBlockAccessList;
}
