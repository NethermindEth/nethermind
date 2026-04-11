// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing.State;
using Nethermind.Int256;
using Nethermind.Logging;

[assembly: InternalsVisibleTo("Nethermind.Evm.Test")]

namespace Nethermind.State;

public class BlockAccessListBasedWorldState(
    IWorldState innerWorldState,
    int blockAccessIndex,
    ILogManager logManager) : IWorldState, IPreBlockCaches
{
    protected IWorldState _innerWorldState = innerWorldState;
    private BlockAccessList? _suggestedBlockAccessList;
    private BlockHeader? _suggestedBlockHeader;
    private readonly TransientStorageProvider _transientStorageProvider = new(logManager);

    public PreBlockCaches Caches => (_innerWorldState as IPreBlockCaches).Caches;

    public bool IsWarmWorldState => (_innerWorldState as IPreBlockCaches)?.IsWarmWorldState ?? false;

    public bool IsInScope => _innerWorldState.IsInScope;
    public IWorldStateScopeProvider ScopeProvider => _innerWorldState.ScopeProvider;
    public Hash256 StateRoot => _innerWorldState.StateRoot;

    public void Setup(Block suggestedBlock)
    {
        _suggestedBlockAccessList = suggestedBlock.BlockAccessList;
        _suggestedBlockHeader = suggestedBlock.Header;
        _transientStorageProvider.Reset();
    }

    public bool HasStateForBlock(BlockHeader? baseBlock)
        => _innerWorldState.HasStateForBlock(baseBlock);

    public void WarmUp(AccessList? accessList)
        => _innerWorldState.WarmUp(accessList);

    public void WarmUp(Address address)
        => _innerWorldState.WarmUp(address);

    public class InvalidBlockLevelAccessListException(BlockHeader block, string message) : InvalidBlockException(block, "InvalidBlockLevelAccessList: " + message);

    public void AddToBalance(Address address, in UInt256 balanceChange, IReleaseSpec spec, out UInt256 oldBalance)
    {
        oldBalance = GetBalanceInternal(address);
    }

    public bool AddToBalanceAndCreateIfNotExists(Address address, in UInt256 balanceChange, IReleaseSpec spec, out UInt256 oldBalance)
    {
        oldBalance = GetBalanceInternal(address);
        return !AccountExistsInternal(address);
    }

    public IDisposable BeginScope(BlockHeader? baseBlock)
        => _innerWorldState.BeginScope(baseBlock);

    public ReadOnlySpan<byte> Get(in StorageCell storageCell)
        => GetInternal(storageCell);

    public byte[] GetOriginal(in StorageCell storageCell)
        => GetOriginalInternal(storageCell);

    public void IncrementNonce(Address address, UInt256 delta, out UInt256 oldNonce)
    {
        oldNonce = GetNonceInternal(address);
    }

    public void SetNonce(Address address, in UInt256 nonce) { }

    public bool InsertCode(Address address, in ValueHash256 codeHash, ReadOnlyMemory<byte> code, IReleaseSpec spec, bool isGenesis = false)
        => true;

    public void Set(in StorageCell storageCell, byte[] newValue) { }

    public UInt256 GetBalance(Address address)
        => GetBalanceInternal(address);

    public UInt256 GetNonce(Address address)
        => GetNonceInternal(address);

    public ValueHash256 GetCodeHash(Address address)
        => GetCodeHashInternal(address);

    public byte[]? GetCode(Address address)
        => GetCodeInternal(address);

    public byte[]? GetCode(in ValueHash256 _)
        => null;

    public void SubtractFromBalance(Address address, in UInt256 balanceChange, IReleaseSpec spec, out UInt256 oldBalance)
    {
        oldBalance = GetBalanceInternal(address);
    }

    public void DeleteAccount(Address address) { }

    public void CreateAccount(Address address, in UInt256 balance, in UInt256 nonce = default) { }

    public void CreateAccountIfNotExists(Address address, in UInt256 balance, in UInt256 nonce = default) { }

    public bool TryGetAccount(Address address, out AccountStruct account)
    {
        account = GetAccountInternal(address) ?? AccountStruct.TotallyEmpty;
        return !account.IsTotallyEmpty;
    }

    public void Restore(Snapshot snapshot) { }

    public Snapshot TakeSnapshot(bool newTransactionStart = false)
        => Snapshot.Empty;

    public bool AccountExists(Address address)
        => AccountExistsInternal(address);

    public bool IsContract(Address address)
        => IsContractInternal(address);

    public bool IsStorageEmpty(Address address)
        => IsStorageEmptyInternal(address);

    public bool IsDeadAccount(Address address)
        => IsDeadAccountInternal(address);

    public void ClearStorage(Address address) { }

    public void Commit(IReleaseSpec releaseSpec, IWorldStateTracer tracer, bool isGenesis = false, bool commitRoots = true) { }

    public void CommitTree(long blockNumber)
        => _innerWorldState.CommitTree(blockNumber);

    public void DecrementNonce(Address address, UInt256 delta)
        => _innerWorldState.DecrementNonce(address, delta);

    public void RecalculateStateRoot() { }

    public void Reset(bool resetBlockChanges = true) { }

    public ArrayPoolList<AddressAsKey> GetAccountChanges() =>
        _suggestedBlockAccessList.AccountChanges.Where(a => a.AccountChanged).Select(a => new AddressAsKey(a.Address)).ToPooledList(_suggestedBlockAccessList.AccountChanges.Count());

    public ReadOnlySpan<byte> GetTransientState(in StorageCell storageCell)
        => _transientStorageProvider.Get(in storageCell);

    public void SetTransientState(in StorageCell storageCell, byte[] newValue)
        => _transientStorageProvider.Set(in storageCell, newValue);

    public void ResetTransient()
        => _transientStorageProvider.Reset();

    private UInt256 GetBalanceInternal(Address address)
    {
        AccountChanges? accountChanges = _suggestedBlockAccessList.GetAccountChanges(address);

        if (accountChanges is not null)
        {
            return accountChanges.GetBalance(blockAccessIndex);
        }

        throw new InvalidBlockLevelAccessListException(_suggestedBlockHeader ?? default, $"Balance access for {address} not in block access list at index {blockAccessIndex}.");
    }

    private UInt256 GetNonceInternal(Address address)
    {
        AccountChanges? accountChanges = _suggestedBlockAccessList.GetAccountChanges(address);

        if (accountChanges is not null)
        {
            return accountChanges.GetNonce(blockAccessIndex);
        }

        throw new InvalidBlockLevelAccessListException(_suggestedBlockHeader ?? default, $"Nonce access for {address} not in block access list at index {blockAccessIndex}.");
    }

    private byte[]? GetCodeInternal(Address address)
    {
        AccountChanges? accountChanges = _suggestedBlockAccessList.GetAccountChanges(address);

        if (accountChanges is not null)
        {
            return accountChanges.GetCode(blockAccessIndex);
        }

        throw new InvalidBlockLevelAccessListException(_suggestedBlockHeader ?? default, $"Code access for {address} not in block access list at index {blockAccessIndex}.");
    }

    private ValueHash256 GetCodeHashInternal(Address address)
        => ValueKeccak.Compute(GetCodeInternal(address));

    private ReadOnlySpan<byte> GetInternal(in StorageCell storageCell)
    {
        AccountChanges? accountChanges = _suggestedBlockAccessList.GetAccountChanges(storageCell.Address);
        accountChanges.TryGetSlotChanges(storageCell.Index, out SlotChanges? slotChanges);

        if (slotChanges is not null)
        {
            return slotChanges.Get(blockAccessIndex);
        }

        throw new InvalidBlockLevelAccessListException(_suggestedBlockHeader ?? default, $"Storage access for {storageCell.Address} not in block access list at index {blockAccessIndex}.");
    }

    private byte[] GetOriginalInternal(in StorageCell storageCell)
    {
        AccountChanges? accountChanges = _suggestedBlockAccessList.GetAccountChanges(storageCell.Address);
        accountChanges.TryGetSlotChanges(storageCell.Index, out SlotChanges? slotChanges);

        if (slotChanges is not null)
        {
            return slotChanges.Get(blockAccessIndex);
        }

        throw new InvalidBlockLevelAccessListException(_suggestedBlockHeader ?? default, $"Storage access for {storageCell.Address} not in block access list at index {blockAccessIndex}.");
    }

    private bool AccountExistsInternal(Address address)
    {
        AccountChanges? accountChanges = _suggestedBlockAccessList.GetAccountChanges(address);
        if (accountChanges is not null)
        {
            // check if existed before current tx
            return accountChanges.AccountExists(blockAccessIndex);
        }

        throw new InvalidBlockLevelAccessListException(_suggestedBlockHeader ?? default, $"Account {address} not found in block access list when checking existence at index {blockAccessIndex}.");
    }

    private bool IsDeadAccountInternal(Address address)
        => !AccountExistsInternal(address) ||
                (GetBalanceInternal(address) == 0 &&
                GetNonceInternal(address) == 0 &&
                GetCodeHashInternal(address) == Keccak.OfAnEmptyString);

    private bool IsContractInternal(Address address)
        => GetCodeHashInternal(address) != Keccak.OfAnEmptyString;

    private bool IsStorageEmptyInternal(Address address)
    {
        // see https://eips.ethereum.org/EIPS/eip-7610
        // storage could only be non-empty for 28 old accounts
        AccountChanges? accountChanges = _suggestedBlockAccessList.GetAccountChanges(address);
        if (accountChanges is not null)
        {
            return accountChanges.EmptyBeforeBlock;
        }

        throw new InvalidBlockLevelAccessListException(_suggestedBlockHeader ?? default, $"Storage empty check for {address} not in block access list at index {blockAccessIndex}.");
    }

    private AccountStruct? GetAccountInternal(Address address)
        => AccountExistsInternal(address) ? new(
                GetNonceInternal(address),
                GetBalanceInternal(address),
                Keccak.EmptyTreeHash, // never used
                GetCodeHashInternal(address)) : null;

    // for testing
    internal IWorldState Inner => _innerWorldState;
}
