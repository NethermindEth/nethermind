// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
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

public class BlockAccessListBasedWorldState(IWorldState innerWorldState, ILogManager logManager) : IWorldState
{
    protected IWorldState _innerWorldState = innerWorldState;
    private BlockAccessList? _suggestedBlockAccessList;
    private BlockHeader? _suggestedBlockHeader;
    private IWorldState? _parentReader;
    private Dictionary<ValueHash256, byte[]>? _codeChangesByHash;
    private uint _blockAccessIndex = 0;
    private EvmWord _readScratch;
    private EvmWord _originalScratch;
    private readonly TransientStorageProvider _transientStorageProvider = new(logManager);

    public void SetBlockAccessIndex(uint index)
    {
        _blockAccessIndex = index;
        _codeChangesByHash = null;
    }

    public bool IsInScope => _innerWorldState.IsInScope;
    public IWorldStateScopeProvider ScopeProvider => _innerWorldState.ScopeProvider;
    public Hash256 StateRoot => _innerWorldState.StateRoot;

    public void Setup(Block suggestedBlock)
    {
        _suggestedBlockAccessList = suggestedBlock.BlockAccessList;
        _suggestedBlockHeader = suggestedBlock.Header;
        _codeChangesByHash = null;
        _transientStorageProvider.Reset();
    }

    public void SetParentReader(IWorldState parentReader)
    {
        if (_parentReader is not null && !ReferenceEquals(_parentReader, parentReader))
        {
            ThrowParentReaderAlreadyAttached();
        }

        _parentReader = parentReader;
    }

    public void ClearParentReader()
    {
        _parentReader = null;
        _suggestedBlockAccessList = null;
        _suggestedBlockHeader = null;
        _codeChangesByHash = null;
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
        (IWorldState parentReader, AccountChanges accountChanges) = ResolveContext(storageCell.Address);

        if (TryGetDeclaredSlotChanges(accountChanges, storageCell.Index, out SlotChanges? slotChanges))
        {
            if (slotChanges is not null && slotChanges.TryGetLastBefore(_blockAccessIndex, out StorageChange storageChange))
            {
                // Copy the BE bytes into per-instance scratch; span valid until the next Get on this instance.
                _readScratch = storageChange.Value;
                return MemoryMarshal.CreateReadOnlySpan(ref Unsafe.As<EvmWord, byte>(ref _readScratch), 32)
                    .WithoutLeadingZeros();
            }

            return parentReader.Get(storageCell);
        }

        ThrowMissingStorage(storageCell);
        return default;
    }

    public ReadOnlySpan<byte> GetOriginal(in StorageCell storageCell)
    {
        (IWorldState parentReader, AccountChanges accountChanges) = ResolveContext(storageCell.Address);

        if (TryGetDeclaredSlotChanges(accountChanges, storageCell.Index, out SlotChanges? slotChanges))
        {
            if (slotChanges is not null && slotChanges.TryGetLastBefore(_blockAccessIndex, out StorageChange storageChange))
            {
                _originalScratch = storageChange.Value;
                return MemoryMarshal.CreateReadOnlySpan(ref Unsafe.As<EvmWord, byte>(ref _originalScratch), 32)
                    .WithoutLeadingZeros();
            }

            return parentReader.GetOriginal(storageCell);
        }

        ThrowMissingStorage(storageCell);
        return default;
    }

    public void IncrementNonce(Address address, UInt256 delta, out UInt256 oldNonce) => oldNonce = GetNonce(address);

    public void SetNonce(Address address, in UInt256 nonce) { }

    public bool InsertCode(Address address, in ValueHash256 codeHash, ReadOnlyMemory<byte> code, IReleaseSpec spec, bool isGenesis = false)
        => true;

    public void Set(in StorageCell storageCell, byte[] newValue) { }

    public UInt256 GetBalance(Address address)
    {
        (IWorldState parentReader, AccountChanges accountChanges) = ResolveContext(address);

        return accountChanges.TryGetLastBalanceChangeBefore(_blockAccessIndex, out BalanceChange balanceChange)
            ? balanceChange.Value
            : parentReader.GetBalance(address);
    }

    public UInt256 GetNonce(Address address)
    {
        (IWorldState parentReader, AccountChanges accountChanges) = ResolveContext(address);

        return accountChanges.TryGetLastNonceChangeBefore(_blockAccessIndex, out NonceChange nonceChange)
            ? nonceChange.Value
            : parentReader.GetNonce(address);
    }

    public ValueHash256 GetCodeHash(Address address)
    {
        (IWorldState parentReader, AccountChanges accountChanges) = ResolveContext(address);

        return accountChanges.TryGetLastCodeChangeBefore(_blockAccessIndex, out CodeChange codeChange)
            ? codeChange.CodeHash
            : parentReader.GetCodeHash(address);
    }

    public byte[]? GetCode(Address address)
    {
        (IWorldState parentReader, AccountChanges accountChanges) = ResolveContext(address);

        return accountChanges.TryGetLastCodeChangeBefore(_blockAccessIndex, out CodeChange codeChange)
            ? codeChange.Code
            : parentReader.GetCode(address);
    }

    public byte[]? GetCode(in ValueHash256 codeHash)
        => TryGetDeclaredCode(in codeHash, out byte[]? code) ? code : null;

    public void SubtractFromBalance(Address address, in UInt256 balanceChange, IReleaseSpec spec, out UInt256 oldBalance) => oldBalance = GetBalance(address);

    public void DeleteAccount(Address address) { }

    public void CreateAccount(Address address, in UInt256 balance, in UInt256 nonce = default) { }

    public void CreateAccountIfNotExists(Address address, in UInt256 balance, in UInt256 nonce = default) { }

    public bool TryGetAccount(Address address, out AccountStruct account)
    {
        (IWorldState parentReader, AccountChanges accountChanges) = ResolveContext(address);

        bool exists = parentReader.TryGetAccount(address, out account);
        UInt256 nonce = exists ? account.Nonce : UInt256.Zero;
        UInt256 balance = exists ? account.Balance : UInt256.Zero;
        ValueHash256 storageRoot = exists ? account.StorageRoot : Keccak.EmptyTreeHash.ValueHash256;
        ValueHash256 codeHash = exists ? account.CodeHash : Keccak.OfAnEmptyString.ValueHash256;

        bool hasPriorChange = false;
        if (accountChanges.TryGetLastNonceChangeBefore(_blockAccessIndex, out NonceChange nonceChange))
        {
            nonce = nonceChange.Value;
            hasPriorChange = true;
        }

        if (accountChanges.TryGetLastBalanceChangeBefore(_blockAccessIndex, out BalanceChange balanceChange))
        {
            balance = balanceChange.Value;
            hasPriorChange = true;
        }

        if (accountChanges.TryGetLastCodeChangeBefore(_blockAccessIndex, out CodeChange codeChange))
        {
            codeHash = codeChange.CodeHash;
            hasPriorChange |= codeChange.Code.Length != 0;
        }

        if (!exists && !hasPriorChange)
        {
            account = AccountStruct.TotallyEmpty;
            return false;
        }

        account = new(nonce, balance, storageRoot, codeHash);
        return true;
    }

    // BAL-backed account/storage mutations are restored by the generated BAL journal;
    // the world-state snapshot only owns transient storage for this wrapper.
    public void Restore(Snapshot snapshot)
        => _transientStorageProvider.Restore(snapshot.StorageSnapshot.TransientStorageSnapshot);

    public Snapshot TakeSnapshot(bool newTransactionStart = false)
    {
        int transientSnapshot = _transientStorageProvider.TakeSnapshot(newTransactionStart);
        return new Snapshot(new Snapshot.Storage(Snapshot.EmptyPosition, transientSnapshot), Snapshot.EmptyPosition);
    }

    public bool AccountExists(Address address)
    {
        (IWorldState parentReader, AccountChanges accountChanges) = ResolveContext(address);

        if (parentReader.AccountExists(address))
        {
            return true;
        }

        if (accountChanges.TryGetLastNonceChangeBefore(_blockAccessIndex, out _))
        {
            return true;
        }

        if (accountChanges.TryGetLastBalanceChangeBefore(_blockAccessIndex, out _))
        {
            return true;
        }

        return accountChanges.TryGetLastCodeChangeBefore(_blockAccessIndex, out CodeChange codeChange) &&
               codeChange.Code.Length != 0;
    }

    public bool IsContract(Address address)
        => GetCodeHash(address) != Keccak.OfAnEmptyString;

    public bool IsStorageEmpty(Address address)
    {
        (IWorldState parentReader, _) = ResolveContext(address);
        return parentReader.IsStorageEmpty(address);
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
            if (accountChanges.HasStateChanges)
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
            ThrowNotInitialized(nameof(_suggestedBlockAccessList));

        if (_suggestedBlockHeader is null)
            ThrowNotInitialized(nameof(_suggestedBlockHeader));
    }

    private IWorldState GetParentReader()
    {
        if (_parentReader is null)
        {
            ThrowNotInitialized(nameof(_parentReader));
        }

        return _parentReader;
    }

    private AccountChanges GetAccountChangesOrThrow(Address address)
    {
        Debug.Assert(_suggestedBlockAccessList is not null);
        AccountChanges? accountChanges = _suggestedBlockAccessList.GetAccountChanges(address);
        if (accountChanges is null)
        {
            ThrowMissingAccount(address);
        }

        return accountChanges;
    }

    private (IWorldState ParentReader, AccountChanges AccountChanges) ResolveContext(Address address)
    {
        CheckInitialized();
        return (GetParentReader(), GetAccountChangesOrThrow(address));
    }

    private bool TryGetDeclaredCode(in ValueHash256 codeHash, [NotNullWhen(true)] out byte[]? code)
    {
        code = null;

        Dictionary<ValueHash256, byte[]> codeChangesByHash = _codeChangesByHash ??= BuildCodeChangesByHash();
        return codeChangesByHash.TryGetValue(codeHash, out code);
    }

    private Dictionary<ValueHash256, byte[]> BuildCodeChangesByHash()
    {
        Dictionary<ValueHash256, byte[]> codeChangesByHash = [];
        if (_suggestedBlockAccessList is null)
        {
            return codeChangesByHash;
        }

        foreach (AccountChanges accountChanges in _suggestedBlockAccessList.AccountChanges)
        {
            if (accountChanges.TryGetLastCodeChangeBefore(_blockAccessIndex, out CodeChange codeChange))
            {
                codeChangesByHash[codeChange.CodeHash] = codeChange.Code;
            }
        }

        return codeChangesByHash;
    }

    private static bool TryGetDeclaredSlotChanges(AccountChanges accountChanges, UInt256 slot, out SlotChanges? slotChanges)
    {
        if (accountChanges.TryGetSlotChanges(slot, out slotChanges))
        {
            return true;
        }

        if (accountChanges.StorageReads.Contains(slot))
        {
            slotChanges = null;
            return true;
        }

        return false;
    }

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowNotInitialized(string fieldName)
        => throw new InvalidOperationException($"{fieldName} was not initialized.");

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowParentReaderAlreadyAttached()
        => throw new InvalidOperationException($"{nameof(_parentReader)} is already attached.");

    [DoesNotReturn, StackTraceHidden]
    private void ThrowMissingAccount(Address address)
        => throw new InvalidBlockLevelAccessListException(_suggestedBlockHeader, $"Suggested block-level access list missing account changes for {address} at index {_blockAccessIndex}.");

    [DoesNotReturn, StackTraceHidden]
    private void ThrowMissingStorage(in StorageCell storageCell)
        => throw new InvalidBlockLevelAccessListException(_suggestedBlockHeader, $"Storage access for {storageCell.Address} not in block access list at index {_blockAccessIndex}.");
}
