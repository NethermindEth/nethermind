// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing.State;
using Nethermind.Int256;
using Nethermind.Logging;

[assembly: InternalsVisibleTo("Nethermind.Evm.Test")]

namespace Nethermind.State;

public class BlockAccessListBasedWorldState(IWorldState innerWorldState, ILogManager logManager) : IWorldState
{
    protected IWorldState _innerWorldState = innerWorldState;
    private BlockAccessList? _suggestedBlockAccessList;
    private BlockHeader? _suggestedBlockHeader;
    private int _blockAccessIndex = 0;
    private readonly TransientStorageProvider _transientStorageProvider = new(logManager);

    public void SetBlockAccessIndex(int index) => _blockAccessIndex = index;

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

    public void AddToBalance(Address address, in UInt256 balanceChange, IReleaseSpec spec, out UInt256 oldBalance) => oldBalance = GetBalance(address);

    public bool AddToBalanceAndCreateIfNotExists(Address address, in UInt256 balanceChange, IReleaseSpec spec, out UInt256 oldBalance)
    {
        oldBalance = GetBalance(address);
        return !AccountExists(address);
    }

    public IDisposable BeginScope(BlockHeader? baseBlock)
        => _innerWorldState.BeginScope(baseBlock);

    public ReadOnlySpan<byte> Get(in StorageCell storageCell)
    {
        CheckInitialized();
        AccountChanges? accountChanges = _suggestedBlockAccessList.GetAccountChanges(storageCell.Address);

        if (accountChanges is not null)
        {
            accountChanges.TryGetSlotChanges(storageCell.Index, out SlotChanges? slotChanges);

            if (slotChanges is not null)
            {
                return slotChanges.Get(_blockAccessIndex);
            }
        }

        throw new InvalidBlockLevelAccessListException(_suggestedBlockHeader, $"Storage access for {storageCell.Address} not in block access list at index {_blockAccessIndex}.");
    }

    public byte[] GetOriginal(in StorageCell storageCell)
    {
        CheckInitialized();
        AccountChanges? accountChanges = _suggestedBlockAccessList.GetAccountChanges(storageCell.Address);

        if (accountChanges is not null)
        {
            accountChanges.TryGetSlotChanges(storageCell.Index, out SlotChanges? slotChanges);

            if (slotChanges is not null)
            {
                return slotChanges.Get(_blockAccessIndex);
            }
        }

        throw new InvalidBlockLevelAccessListException(_suggestedBlockHeader, $"Storage access for {storageCell.Address} not in block access list at index {_blockAccessIndex}.");
    }

    public void IncrementNonce(Address address, UInt256 delta, out UInt256 oldNonce) => oldNonce = GetNonce(address);

    public void SetNonce(Address address, in UInt256 nonce) { }

    public bool InsertCode(Address address, in ValueHash256 codeHash, ReadOnlyMemory<byte> code, IReleaseSpec spec, bool isGenesis = false)
        => true;

    public void Set(in StorageCell storageCell, byte[] newValue) { }

    public UInt256 GetBalance(Address address)
    {
        CheckInitialized();

        AccountChanges? accountChanges = _suggestedBlockAccessList.GetAccountChanges(address);

        if (accountChanges is not null)
        {
            return accountChanges.GetBalance(_blockAccessIndex)
                ?? throw new InvalidBlockLevelAccessListException(_suggestedBlockHeader,
                    $"Suggested block-level access list missing balance for {address} at index {_blockAccessIndex}.");
        }

        throw new InvalidBlockLevelAccessListException(_suggestedBlockHeader, $"Suggested block-level access list missing account changes for {address} at index {_blockAccessIndex}.");
    }

    public UInt256 GetNonce(Address address)
    {
        CheckInitialized();

        AccountChanges? accountChanges = _suggestedBlockAccessList.GetAccountChanges(address);

        if (accountChanges is not null)
        {
            return accountChanges.GetNonce(_blockAccessIndex)
                ?? throw new InvalidBlockLevelAccessListException(_suggestedBlockHeader,
                    $"Suggested block-level access list missing nonce for {address} at index {_blockAccessIndex}.");
        }

        throw new InvalidBlockLevelAccessListException(_suggestedBlockHeader, $"Suggested block-level access list missing account changes for {address} at index {_blockAccessIndex}.");
    }

    public ValueHash256 GetCodeHash(Address address)
    {
        CheckInitialized();

        AccountChanges? accountChanges = _suggestedBlockAccessList.GetAccountChanges(address);

        if (accountChanges is not null)
        {
            return accountChanges.GetCodeHash(_blockAccessIndex);
        }

        throw new InvalidBlockLevelAccessListException(_suggestedBlockHeader, $"Suggested block-level access list missing account changes for {address} at index {_blockAccessIndex}.");
    }

    public byte[]? GetCode(Address address)
    {
        CheckInitialized();

        AccountChanges? accountChanges = _suggestedBlockAccessList.GetAccountChanges(address);

        if (accountChanges is not null)
        {
            return accountChanges.GetCode(_blockAccessIndex);
        }

        throw new InvalidBlockLevelAccessListException(_suggestedBlockHeader, $"Suggested block-level access list missing account changes for {address} at index {_blockAccessIndex}.");
    }

    public byte[]? GetCode(in ValueHash256 _)
        => null;

    public void SubtractFromBalance(Address address, in UInt256 balanceChange, IReleaseSpec spec, out UInt256 oldBalance) => oldBalance = GetBalance(address);

    public void DeleteAccount(Address address) { }

    public void CreateAccount(Address address, in UInt256 balance, in UInt256 nonce = default) { }

    public void CreateAccountIfNotExists(Address address, in UInt256 balance, in UInt256 nonce = default) { }

    public bool TryGetAccount(Address address, out AccountStruct account)
    {
        if (AccountExists(address))
        {
            account = new(
                GetNonce(address),
                GetBalance(address),
                Keccak.EmptyTreeHash, // never used
                GetCodeHash(address));
            return true;
        }

        account = AccountStruct.TotallyEmpty;
        return false;
    }

    public void Restore(Snapshot snapshot) { }

    public Snapshot TakeSnapshot(bool newTransactionStart = false)
        => Snapshot.Empty;

    public bool AccountExists(Address address)
    {
        CheckInitialized();

        AccountChanges? accountChanges = _suggestedBlockAccessList.GetAccountChanges(address);
        if (accountChanges is not null)
        {
            // check if existed before current tx
            return accountChanges.AccountExists(_blockAccessIndex);
        }

        throw new InvalidBlockLevelAccessListException(_suggestedBlockHeader, $"Suggested block-level access list missing account changes for {address} at index {_blockAccessIndex}.");
    }

    public bool IsContract(Address address)
        => GetCodeHash(address) != Keccak.OfAnEmptyString;

    public bool IsStorageEmpty(Address address)
    {
        CheckInitialized();

        // see https://eips.ethereum.org/EIPS/eip-7610
        // storage could only be non-empty for 28 old accounts
        AccountChanges? accountChanges = _suggestedBlockAccessList.GetAccountChanges(address);
        if (accountChanges is not null)
        {
            return accountChanges.EmptyBeforeBlock;
        }

        throw new InvalidBlockLevelAccessListException(_suggestedBlockHeader, $"Suggested block-level access list missing account changes for {address} at index {_blockAccessIndex}.");
    }

    public bool IsDeadAccount(Address address)
        => !AccountExists(address) ||
                (GetBalance(address) == 0 &&
                GetNonce(address) == 0 &&
                GetCodeHash(address) == Keccak.OfAnEmptyString);

    public void ClearStorage(Address address) { }

    public void Commit(IReleaseSpec releaseSpec, IWorldStateTracer tracer, bool isGenesis = false, bool commitRoots = true) { }

    public void CommitTree(long blockNumber)
        => _innerWorldState.CommitTree(blockNumber);

    public void DecrementNonce(Address address, UInt256 delta) { }

    public void RecalculateStateRoot() { }

    public void Reset(bool resetBlockChanges = true) { }

    public ArrayPoolList<AddressAsKey> GetAccountChanges()
    {
        CheckInitialized();

        ArrayPoolList<AddressAsKey> result = new(_suggestedBlockAccessList.AccountChanges.Count);
        foreach (AccountChanges accountChanges in _suggestedBlockAccessList.AccountChanges)
        {
            if (accountChanges.AccountChanged)
            {
                result.Add(new AddressAsKey(accountChanges.Address));
            }
        }
        return result;
    }

    public ReadOnlySpan<byte> GetTransientState(in StorageCell storageCell)
        => _transientStorageProvider.Get(in storageCell);

    public void SetTransientState(in StorageCell storageCell, byte[] newValue)
        => _transientStorageProvider.Set(in storageCell, newValue);

    public void ResetTransient()
        => _transientStorageProvider.Reset();

    public void CreateEmptyAccountIfDeleted(Address address)
        => _innerWorldState.CreateEmptyAccountIfDeleted(address);

    private void CheckInitialized()
    {
        if (_suggestedBlockAccessList is null)
            throw new InvalidOperationException($"{nameof(_suggestedBlockAccessList)} was not initialized.");

        if (_suggestedBlockHeader is null)
            throw new InvalidOperationException($"{nameof(_suggestedBlockHeader)} was not initialized.");
    }
}
