// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing.State;
using Nethermind.Int256;

namespace Nethermind.State;

public class ParallelWorldState(IWorldState innerWorldState, bool enableParallelExecution) : WrappedWorldState(innerWorldState), IBlockAccessListBuilder, IPreBlockCaches
{
    public bool TracingEnabled { get; set; } = false;
    public bool ParallelExecutionEnabled => TracingEnabled && enableParallelExecution;

    public BlockAccessList GeneratedBlockAccessList { get; set; } = new();
    private BlockAccessList _suggestedBlockAccessList;
    private BlockAccessList[] _intermediateBlockAccessLists;
    private TransientStorageProvider[] _transientStorageProviders;

    public PreBlockCaches Caches => (_innerWorldState as IPreBlockCaches).Caches;

    public bool IsWarmWorldState => (_innerWorldState as IPreBlockCaches).IsWarmWorldState;

    public void SetupGeneratedAccessLists(int txCount)
    {
        _intermediateBlockAccessLists = new BlockAccessList[txCount + 1];
        _transientStorageProviders = new TransientStorageProvider[txCount + 1];
    }

    public void LoadSuggestedBlockAccessList(BlockAccessList suggested)
    {
        LoadPreBlockState(suggested);
        _suggestedBlockAccessList = suggested;
    }

    private void LoadPreBlockState(BlockAccessList blockAccessList)
    {
        foreach (AccountChanges accountChanges in blockAccessList.AccountChanges)
        {
            // check if changed before loading prestate
            accountChanges.CheckWasChanged();

            bool exists = _innerWorldState.TryGetAccount(accountChanges.Address, out AccountStruct account);
            accountChanges.ExistedBeforeBlock = exists;

            accountChanges.AddBalanceChange(new(-1, account.Balance));
            accountChanges.AddNonceChange(new(-1, (ulong)account.Nonce));
            // accountChanges.CodeHash = account.CodeHash;

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
                UInt256 oldBalance = _innerWorldState.GetBalance(accountChanges.Address) ;
                UInt256 newBalance = accountChanges.BalanceChanges.Last().PostBalance;
                _innerWorldState.AddToBalance(accountChanges.Address, oldBalance - newBalance, spec);
            }

            if (accountChanges.NonceChanges.Count > 0)
            {
                _innerWorldState.SetNonce(accountChanges.Address, accountChanges.NonceChanges.Last().NewNonce);
            }

            if (accountChanges.CodeChanges.Count > 0)
            {
                _innerWorldState.InsertCode(accountChanges.Address, accountChanges.CodeChanges.Last().NewCode, spec);
            }

            foreach (SlotChanges slotChange in accountChanges.StorageChanges)
            {
                StorageCell storageCell = new(accountChanges.Address, new(slotChange.Slot));
                _innerWorldState.Set(storageCell, slotChange.Changes.Last().NewValue);
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
        // combine intermediate BALs and receipt tracers
    }

    public override void AddToBalance(Address address, in UInt256 balanceChange, IReleaseSpec spec, int? blockAccessIndex = null)
        => AddToBalance(address, balanceChange, spec, out _);

    public override void AddToBalance(Address address, in UInt256 balanceChange, IReleaseSpec spec, out UInt256 oldBalance, int? blockAccessIndex = null)
    {
        if (ParallelExecutionEnabled)
        {
            oldBalance = GetBalanceInternal(address, blockAccessIndex.Value);
        }
        else
        {
            _innerWorldState.AddToBalance(address, balanceChange, spec, out oldBalance);
        }

        if (TracingEnabled)
        {
            UInt256 newBalance = oldBalance + balanceChange;
            GeneratedBlockAccessList.AddBalanceChange(address, oldBalance, newBalance);
        }
    }

    public override bool AddToBalanceAndCreateIfNotExists(Address address, in UInt256 balanceChange, IReleaseSpec spec, int? blockAccessIndex = null)
        => AddToBalanceAndCreateIfNotExists(address, balanceChange, spec, out _);

    public override bool AddToBalanceAndCreateIfNotExists(Address address, in UInt256 balanceChange, IReleaseSpec spec, out UInt256 oldBalance, int? blockAccessIndex = null)
    {
        if (!blockAccessIndex.HasValue)
            throw new ArgumentNullException(nameof(blockAccessIndex));

        bool res;
        if (ParallelExecutionEnabled)
        {
            res = !AccountExistsInternal(address, blockAccessIndex.Value);
            oldBalance = GetBalanceInternal(address, blockAccessIndex.Value);
        }
        else
        {
            res = _innerWorldState.AddToBalanceAndCreateIfNotExists(address, balanceChange, spec, out oldBalance);
        }

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

    public override ReadOnlySpan<byte> Get(in StorageCell storageCell, int? blockAccessIndex = null)
    {
        if (!blockAccessIndex.HasValue)
            throw new ArgumentNullException(nameof(blockAccessIndex));

        if (TracingEnabled)
        {
            GeneratedBlockAccessList.AddStorageRead(storageCell);
        }
        return GetInternal(storageCell, blockAccessIndex.Value);
    }

    public override byte[] GetOriginal(in StorageCell storageCell, int? blockAccessIndex = null)
    {
        if (!blockAccessIndex.HasValue)
            throw new ArgumentNullException(nameof(blockAccessIndex));

        if (TracingEnabled)
        {
            GeneratedBlockAccessList.AddStorageRead(storageCell);
        }
        return GetOriginalInternal(storageCell, blockAccessIndex.Value);
    }

    public override void IncrementNonce(Address address, UInt256 delta, int? blockAccessIndex = null)
        => IncrementNonce(address, delta, out _);

    public override void IncrementNonce(Address address, UInt256 delta, out UInt256 oldNonce, int? blockAccessIndex = null)
    {
        if (!blockAccessIndex.HasValue)
            throw new ArgumentNullException(nameof(blockAccessIndex));

        if (ParallelExecutionEnabled)
        {
            oldNonce = GetNonceInternal(address, blockAccessIndex.Value);
        }
        else
        {
            _innerWorldState.IncrementNonce(address, delta, out oldNonce);
        }

        if (TracingEnabled)
        {
            GeneratedBlockAccessList.AddNonceChange(address, (ulong)(oldNonce + delta));
        }
    }

    public override void SetNonce(Address address, in UInt256 nonce)
    {
        if (!ParallelExecutionEnabled)
        {
            _innerWorldState.SetNonce(address, nonce);
        }

        if (TracingEnabled)
        {
            GeneratedBlockAccessList.AddNonceChange(address, (ulong)nonce);
        }
    }

    public override bool InsertCode(Address address, in ValueHash256 codeHash, ReadOnlyMemory<byte> code, IReleaseSpec spec, bool isGenesis = false, int? blockAccessIndex = null)
    {
        if (!blockAccessIndex.HasValue)
            throw new ArgumentNullException(nameof(blockAccessIndex));

        if (TracingEnabled)
        {
            byte[] oldCode = GetCodeInternal(address, blockAccessIndex.Value) ?? [];
            GeneratedBlockAccessList.AddCodeChange(address, oldCode, code.ToArray());
        }
        return ParallelExecutionEnabled && _innerWorldState.InsertCode(address, codeHash, code, spec, isGenesis);
    }

    public override void Set(in StorageCell storageCell, byte[] newValue, int? blockAccessIndex = null)
    {
        if (!blockAccessIndex.HasValue)
            throw new ArgumentNullException(nameof(blockAccessIndex));

        if (TracingEnabled)
        {
            ReadOnlySpan<byte> oldValue = GetInternal(storageCell, blockAccessIndex.Value);
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

        if (TracingEnabled)
        {
            AddAccountRead(address);
        }
        return GetBalanceInternal(address, blockAccessIndex.Value);
    }

    public override UInt256 GetNonce(Address address, int? blockAccessIndex = null)
    {
        if (!blockAccessIndex.HasValue)
            throw new ArgumentNullException(nameof(blockAccessIndex));

        if (TracingEnabled)
        {
            AddAccountRead(address);
        }
        return GetNonceInternal(address, blockAccessIndex.Value);
    }

    public override ValueHash256 GetCodeHash(Address address, int? blockAccessIndex = null)
    {
        if (!blockAccessIndex.HasValue)
            throw new ArgumentNullException(nameof(blockAccessIndex));

        if (TracingEnabled)
        {
            AddAccountRead(address);
        }
        return GetCodeHashInternal(address, blockAccessIndex.Value);
    }

    public override byte[]? GetCode(Address address, int? blockAccessIndex = null)
    {
        if (!blockAccessIndex.HasValue)
            throw new ArgumentNullException(nameof(blockAccessIndex));

        if (TracingEnabled)
        {
            AddAccountRead(address);
        }
        return GetCodeInternal(address, blockAccessIndex.Value);
    }

    public override void SubtractFromBalance(Address address, in UInt256 balanceChange, IReleaseSpec spec, int? blockAccessIndex = null)
    {
        if (!blockAccessIndex.HasValue)
            throw new ArgumentNullException(nameof(blockAccessIndex));

        if (TracingEnabled)
        {
            UInt256 before = GetBalanceInternal(address, blockAccessIndex.Value);
            UInt256 after = before - balanceChange;
            GeneratedBlockAccessList.AddBalanceChange(address, before, after);
        }
        if (!ParallelExecutionEnabled)
        {
            _innerWorldState.SubtractFromBalance(address, balanceChange, spec);
        }
    }

    public override void DeleteAccount(Address address, int? blockAccessIndex = null)
    {
        if (!blockAccessIndex.HasValue)
            throw new ArgumentNullException(nameof(blockAccessIndex));

        if (TracingEnabled)
        {
            GeneratedBlockAccessList.DeleteAccount(address, GetBalanceInternal(address, blockAccessIndex.Value));
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
            AddAccountRead(address);
            // todo move inside bal
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

    public override void CreateAccountIfNotExists(Address address, in UInt256 balance, in UInt256 nonce = default, int? blockAccessIndex = null)
    {
        if (!blockAccessIndex.HasValue)
            throw new ArgumentNullException(nameof(blockAccessIndex));

        if (!AccountExistsInternal(address, blockAccessIndex.Value))
        {
            CreateAccount(address, balance, nonce);
        }
    }

    public override bool TryGetAccount(Address address, out AccountStruct account, int? blockAccessIndex = null)
    {
        if (!blockAccessIndex.HasValue)
            throw new ArgumentNullException(nameof(blockAccessIndex));

        if (TracingEnabled)
        {
            AddAccountRead(address);
        }
        
        if (ParallelExecutionEnabled)
        {
            account = GetAccountInternal(address, blockAccessIndex.Value) ?? AccountStruct.TotallyEmpty;
            return !account.IsTotallyEmpty;
        }
        else
        {
            return _innerWorldState.TryGetAccount(address, out account);
        }
    }

    public void AddAccountRead(Address address, int? blockAccessIndex = null)
        => GetGeneratingBlockAccessList(blockAccessIndex).AddAccountRead(address);

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

    public override bool AccountExists(Address address, int? blockAccessIndex = null)
    {
        if (!blockAccessIndex.HasValue)
            throw new ArgumentNullException(nameof(blockAccessIndex));

        if (TracingEnabled)
        {
            AddAccountRead(address);
        }
        return AccountExistsInternal(address, blockAccessIndex.Value);
    }

    public override bool IsContract(Address address, int? blockAccessIndex = null)
    {
        if (!blockAccessIndex.HasValue)
            throw new ArgumentNullException(nameof(blockAccessIndex));

        if (TracingEnabled)
        {
            AddAccountRead(address);
        }
        return IsContractInternal(address, blockAccessIndex.Value);
    }

    public override bool IsStorageEmpty(Address address, int? blockAccessIndex = null)
    {
        if (!blockAccessIndex.HasValue)
            throw new ArgumentNullException(nameof(blockAccessIndex));

        // don't need to add read, already added in IsNonZeroAccount
    
        return IsStorageEmptyInternal(address, blockAccessIndex.Value);
    }

    public override bool IsDeadAccount(Address address, int? blockAccessIndex = null)
    {
        if (!blockAccessIndex.HasValue)
            throw new ArgumentNullException(nameof(blockAccessIndex));

        if (TracingEnabled)
        {
            AddAccountRead(address);
        }
        return IsDeadAccountInternal(address, blockAccessIndex.Value);
    }

    public override void ClearStorage(Address address)
    {
        if (TracingEnabled)
        {
            AddAccountRead(address);
        }
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

    public override void Reset(bool resetBlockChanges = true)
    {
        if (!ParallelExecutionEnabled)
        {
            _innerWorldState.Reset(resetBlockChanges);
        }
    }

    public override ArrayPoolList<AddressAsKey> GetAccountChanges() =>
        ParallelExecutionEnabled ?
            _suggestedBlockAccessList.AccountChanges.Where(a => a.AccountChanged).Select(a => new AddressAsKey(a.Address)).ToPooledList(_suggestedBlockAccessList.AccountChanges.Count) :
            _innerWorldState.GetAccountChanges();
    
    public override ReadOnlySpan<byte> GetTransientState(in StorageCell storageCell, int? blockAccessIndex = null)
    {
        if (!blockAccessIndex.HasValue)
            throw new ArgumentNullException(nameof(blockAccessIndex));
        
        return ParallelExecutionEnabled ?
            _transientStorageProviders[blockAccessIndex.Value].Get(in storageCell) :
            _innerWorldState.GetTransientState(in storageCell);
    }

    public override void SetTransientState(in StorageCell storageCell,  byte[] newValue, int? blockAccessIndex = null)
    {
        if (!blockAccessIndex.HasValue)
            throw new ArgumentNullException(nameof(blockAccessIndex));
        
        if (ParallelExecutionEnabled)
        {
            _transientStorageProviders[blockAccessIndex.Value].Set(in storageCell, newValue);
        }
        else
        {
            _innerWorldState.SetTransientState(in storageCell, newValue);
        }
    }

    public override void ResetTransient(int? blockAccessIndex = null)
    {
        if (!blockAccessIndex.HasValue)
            throw new ArgumentNullException(nameof(blockAccessIndex));
        
        if (ParallelExecutionEnabled)
        {
            _transientStorageProviders[blockAccessIndex.Value].Reset();
        }
        else
        {
            _innerWorldState.ResetTransient();
        }
    }

    private BlockAccessList GetGeneratingBlockAccessList(int? blockAccessIndex = null)
        => ParallelExecutionEnabled ? _intermediateBlockAccessLists[blockAccessIndex!.Value] : GeneratedBlockAccessList;

    private UInt256 GetBalanceInternal(Address address, int blockAccessIndex)
    {
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
                return accountChanges.GetBalance(blockAccessIndex);
            }

            // should never happen
            Debug.Fail("Could not find balance during parallel execution");
            return 0;
        }
        else
        {
            return _innerWorldState.GetBalance(address);
        }
    }

    private UInt256 GetNonceInternal(Address address, int blockAccessIndex)
    {
        if (ParallelExecutionEnabled)
        {
            // get from BAL -> suggested block -> inner world state
            AccountChanges? accountChanges = GetGeneratingBlockAccessList().GetAccountChanges(address);
            if (accountChanges is not null && accountChanges.NonceChanges.Count == 1)
            {
                return accountChanges.NonceChanges.First().NewNonce;
            }

            accountChanges = _suggestedBlockAccessList.GetAccountChanges(address);

            if (accountChanges is not null && accountChanges.NonceChanges.Count > 0)
            {
                return accountChanges.GetNonce(blockAccessIndex);
            }

            Debug.Fail("Could not find nonce during parallel execution");
            return 0;
        }
        else
        {
            return _innerWorldState.GetNonce(address);
        }
    }

    private byte[]? GetCodeInternal(Address address, int blockAccessIndex)
    {
        if (ParallelExecutionEnabled)
        {
            // get from BAL -> suggested block -> inner world state
            AccountChanges? accountChanges = GetGeneratingBlockAccessList().GetAccountChanges(address);
            if (accountChanges is not null && accountChanges.CodeChanges.Count == 1)
            {
                return accountChanges.CodeChanges.First().NewCode;
            }

            accountChanges = _suggestedBlockAccessList.GetAccountChanges(address);

            if (accountChanges is not null && accountChanges.NonceChanges.Count > 0)
            {
                return accountChanges.GetCode(blockAccessIndex);
            }

            Debug.Fail("Could not find code during parallel execution");
            return [];
        }
        else
        {
            return _innerWorldState.GetCode(address);
        }
    }

    private ValueHash256 GetCodeHashInternal(Address address, int blockAccessIndex)
        => ParallelExecutionEnabled ?
                ValueKeccak.Compute(GetCodeInternal(address, blockAccessIndex)) :
                _innerWorldState.GetCodeHash(address);

    private ReadOnlySpan<byte> GetInternal(in StorageCell storageCell, int blockAccessIndex)
    {
        if (ParallelExecutionEnabled)
        {
            // get from BAL -> suggested block -> inner world state
            AccountChanges? accountChanges = GetGeneratingBlockAccessList().GetAccountChanges(storageCell.Address);
            accountChanges.TryGetSlotChanges(storageCell.Index.ToBigEndian(), out SlotChanges? slotChanges);

            if (slotChanges is not null && slotChanges.Changes.Count == 1)
            {
                return slotChanges.Changes.First().NewValue;
            }

            accountChanges = _suggestedBlockAccessList.GetAccountChanges(storageCell.Address);
            accountChanges.TryGetSlotChanges(storageCell.Index.ToBigEndian(), out slotChanges);

            if (slotChanges is not null && slotChanges.Changes.Count > 0)
            {
                return slotChanges.Get(blockAccessIndex);
            }

            Debug.Fail("Could not find storage value during parallel execution");
            return [];
        }
        else
        {
            return _innerWorldState.Get(storageCell);
        }
    }

    private byte[] GetOriginalInternal(in StorageCell storageCell, int blockAccessIndex)
    {
        if (ParallelExecutionEnabled)
        {
            AccountChanges? accountChanges = _suggestedBlockAccessList.GetAccountChanges(storageCell.Address);
            accountChanges.TryGetSlotChanges(storageCell.Index.ToBigEndian(), out SlotChanges? slotChanges);

            if (slotChanges is not null && slotChanges.Changes.Count > 0)
            {
                return slotChanges.Get(blockAccessIndex);
            }

            Debug.Fail("Could not find storage value during parallel execution");
            return [];
        }
        else
        {
            return _innerWorldState.GetOriginal(storageCell);
        }
    }

    private bool AccountExistsInternal(Address address, int blockAccessIndex)
    {
        if (ParallelExecutionEnabled)
        {
            AccountChanges? accountChanges = GetGeneratingBlockAccessList().GetAccountChanges(address);
            if (accountChanges is not null && accountChanges.NonceChanges.Count == 1)
            {
                // if nonce is changed in this tx must exists
                // could have been created this tx
                return true;
            }

            accountChanges = _suggestedBlockAccessList.GetAccountChanges(address);
            if (accountChanges is not null)
            {
                // check if existed before current tx
                return accountChanges.AccountExists(blockAccessIndex);
            }

            Debug.Fail("Could not find nonce during parallel execution");
            return false;
        }
        else
        {
            return _innerWorldState.AccountExists(address);
        }
    }

    private bool IsDeadAccountInternal(Address address, int blockAccessIndex)
        => ParallelExecutionEnabled ?
                !AccountExistsInternal(address, blockAccessIndex) ||
                (
                    GetBalanceInternal(address, blockAccessIndex) == 0 &&
                    GetNonceInternal(address, blockAccessIndex) == 0 &&
                    GetCodeHashInternal(address, blockAccessIndex) == Keccak.OfAnEmptyString) :
                _innerWorldState.IsDeadAccount(address);

    private bool IsContractInternal(Address address, int blockAccessIndex)
        => ParallelExecutionEnabled ?
                GetCodeHashInternal(address, blockAccessIndex) != Keccak.OfAnEmptyString :
                _innerWorldState.IsContract(address);

    private bool IsStorageEmptyInternal(Address address, int blockAccessIndex)
    {
        if (ParallelExecutionEnabled)
        {
            AccountChanges? accountChanges = GetGeneratingBlockAccessList().GetAccountChanges(address);
            HashSet<byte[]> zeroedSlots = [];
            foreach (SlotChanges slotChanges in accountChanges.StorageChanges)
            {
                if (!slotChanges.Changes.Last().NewValue.IsZero())
                {
                    return false;
                }
                zeroedSlots.Add(slotChanges.Slot);
            }

            accountChanges = _suggestedBlockAccessList.GetAccountChanges(address);
            if (accountChanges is not null)
            {
                HashSet<byte[]> allSlots = accountChanges.GetAllSlots(blockAccessIndex);
                return allSlots.SetEquals(zeroedSlots);
            }

            // todo fix error handling
            Debug.Fail("Could not find nonce during parallel execution");
            return false;
        }
        else
        {
            return _innerWorldState.IsStorageEmpty(address);
        }
    }

    private AccountStruct? GetAccountInternal(Address address, int blockAccessIndex)
        => AccountExistsInternal(address, blockAccessIndex) ? new(
                GetNonceInternal(address, blockAccessIndex),
                GetBalanceInternal(address, blockAccessIndex),
                Keccak.EmptyTreeHash, // never used
                GetCodeHashInternal(address, blockAccessIndex)) : null;
}
