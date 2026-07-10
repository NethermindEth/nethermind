// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Core.Exceptions;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing.State;
using Nethermind.Int256;
using Nethermind.Logging;

[assembly: InternalsVisibleTo("Nethermind.Evm.Test")]

namespace Nethermind.State;

public class BlockAccessListBasedWorldState(IWorldState state, ILogManager logManager) : WorldStateDecorator(state)
{
    private ReadOnlyBlockAccessList? _suggestedBlockAccessList;
    private BlockHeader? _suggestedBlockHeader;
    private IWorldState? _parentReader;
    private Dictionary<ValueHash256, (uint Index, byte[] Code)>? _codeChangesByHash;
    private uint _blockAccessIndex = 0;
    private EvmWord _readScratch;
    private EvmWord _originalScratch;
    private EvmWord _transientScratch;
    private UInt256 _scratchBalance;
    private ValueHash256 _scratchCodeHash;
    private readonly TransientStorageProvider _transientStorageProvider = new(logManager);

    // _codeChangesByHash is monotonic across the block (a code blob deployed at any tx is
    // queryable at any later tx), so it is built once per block in Setup and not rebuilt here.
    public void SetBlockAccessIndex(uint index) => _blockAccessIndex = index;

    public void Setup(Block suggestedBlock)
    {
        _suggestedBlockAccessList = suggestedBlock.BlockAccessList;
        _suggestedBlockHeader = suggestedBlock.Header;
        _codeChangesByHash = BuildCodeChangesByHash();
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

    public class InvalidBlockLevelAccessListException(BlockHeader block, string message) : InvalidBlockException(block, "InvalidBlockLevelAccessList: " + message);

    public override void AddToBalance(Address address, in UInt256 balanceChange, IReleaseSpec spec, out UInt256 oldBalance) => oldBalance = GetBalance(address);

    public override bool AddToBalanceAndCreateIfNotExists(Address address, in UInt256 balanceChange, IReleaseSpec spec, out UInt256 oldBalance)
    {
        oldBalance = GetBalance(address);
        return !AccountExists(address);
    }

    public override ReadOnlySpan<byte> Get(in StorageCell storageCell)
    {
        (IWorldState parentReader, ReadOnlyAccountChanges accountChanges) = ResolveContext(storageCell.Address);

        if (TryGetDeclaredSlotChanges(accountChanges, storageCell.Index, out ReadOnlySlotChanges? slotChanges))
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

    public override ReadOnlySpan<byte> GetOriginal(in StorageCell storageCell)
    {
        (IWorldState parentReader, ReadOnlyAccountChanges accountChanges) = ResolveContext(storageCell.Address);

        if (TryGetDeclaredSlotChanges(accountChanges, storageCell.Index, out ReadOnlySlotChanges? slotChanges))
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

    public override void IncrementNonce(Address address, ulong delta, out ulong oldNonce) => oldNonce = GetNonce(address);

    public override void SetNonce(Address address, in ulong nonce) { }

    public override bool InsertCode(Address address, in ValueHash256 codeHash, ReadOnlyMemory<byte> code, IReleaseSpec spec, bool isGenesis = false)
        => true;

    public override void Set(in StorageCell storageCell, byte[] newValue) { }

    /// <inheritdoc cref="IWorldState.SStore"/>
    /// <remarks>
    /// Reports the comparisons without writing: this state replays a block access list, so <see cref="Set"/> is
    /// a no-op and delegating to the wrapped state would mutate it.
    /// </remarks>
    public override SStoreState SStore(in StorageCell storageCell, in EvmWord newValue)
    {
        ReadOnlySpan<byte> newBytes = StorageWord.ToStorageBytes(in newValue, out bool newIsZero);
        ReadOnlySpan<byte> currentValue = Get(in storageCell);
        bool currentIsZero = currentValue.IsZero();
        bool newSameAsCurrent = (newIsZero && currentIsZero) || Bytes.AreEqual(currentValue, newBytes);

        SStoreState state = currentIsZero ? SStoreState.CurrentIsZero : SStoreState.None;
        if (newSameAsCurrent) return state | SStoreState.NewSameAsCurrent;

        ReadOnlySpan<byte> originalValue = GetOriginal(in storageCell);
        if (originalValue.IsZero()) state |= SStoreState.OriginalIsZero;
        if (Bytes.AreEqual(originalValue, currentValue)) state |= SStoreState.CurrentSameAsOriginal;
        if (Bytes.AreEqual(originalValue, newBytes)) state |= SStoreState.NewSameAsOriginal;

        return state;
    }

    public override ref readonly UInt256 GetBalance(Address address)
    {
        (IWorldState parentReader, ReadOnlyAccountChanges accountChanges) = ResolveContext(address);

        if (accountChanges.TryGetLastBalanceChangeBefore(_blockAccessIndex, out BalanceChange balanceChange))
        {
            _scratchBalance = balanceChange.Value;
            return ref _scratchBalance;
        }
        return ref parentReader.GetBalance(address);
    }

    public override ulong GetNonce(Address address)
    {
        (IWorldState parentReader, ReadOnlyAccountChanges accountChanges) = ResolveContext(address);

        return accountChanges.TryGetLastNonceChangeBefore(_blockAccessIndex, out NonceChange nonceChange)
            ? nonceChange.Value
            : parentReader.GetNonce(address);
    }

    public override ref readonly ValueHash256 GetCodeHash(Address address)
    {
        (IWorldState parentReader, ReadOnlyAccountChanges accountChanges) = ResolveContext(address);

        if (accountChanges.TryGetLastCodeChangeBefore(_blockAccessIndex, out CodeChange codeChange))
        {
            _scratchCodeHash = codeChange.CodeHash;
            return ref _scratchCodeHash;
        }
        return ref parentReader.GetCodeHash(address);
    }

    public override byte[]? GetCode(Address address)
    {
        (IWorldState parentReader, ReadOnlyAccountChanges accountChanges) = ResolveContext(address);

        return accountChanges.TryGetLastCodeChangeBefore(_blockAccessIndex, out CodeChange codeChange)
            ? codeChange.Code
            : parentReader.GetCode(address);
    }

    public override byte[]? GetCode(in ValueHash256 codeHash)
        => TryGetDeclaredCode(in codeHash, out byte[]? code) ? code : null;

    public override void SubtractFromBalance(Address address, in UInt256 balanceChange, IReleaseSpec spec, out UInt256 oldBalance) => oldBalance = GetBalance(address);

    public override void DeleteAccount(Address address) { }

    public override void CreateAccount(Address address, in UInt256 balance, in ulong nonce = default) { }

    public override void CreateAccountIfNotExists(Address address, in UInt256 balance, in ulong nonce = default) { }

    public override bool TryGetAccount(Address address, out AccountStruct account)
    {
        (IWorldState parentReader, ReadOnlyAccountChanges accountChanges) = ResolveContext(address);

        bool exists = parentReader.TryGetAccount(address, out account);
        ulong nonce = exists ? account.Nonce : 0;
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
    public override void Restore(Snapshot snapshot)
        => _transientStorageProvider.Restore(snapshot.StorageSnapshot.TransientStorageSnapshot);

    public override Snapshot TakeSnapshot(bool newTransactionStart = false)
    {
        int transientSnapshot = _transientStorageProvider.TakeSnapshot(newTransactionStart);
        return new Snapshot(new Snapshot.Storage(Snapshot.EmptyPosition, transientSnapshot), Snapshot.EmptyPosition);
    }

    public override bool AccountExists(Address address)
    {
        (IWorldState parentReader, ReadOnlyAccountChanges accountChanges) = ResolveContext(address);

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

    public override bool IsContract(Address address)
        => GetCodeHash(address) != Keccak.OfAnEmptyString;

    public override bool IsStorageEmpty(Address address)
    {
        (IWorldState parentReader, _) = ResolveContext(address);
        return parentReader.IsStorageEmpty(address);
    }

    public override bool IsDeadAccount(Address address)
        => !AccountExists(address) ||
                (GetBalance(address) == 0 &&
                GetNonce(address) == 0 &&
                GetCodeHash(address) == Keccak.OfAnEmptyString);

    public override void ClearStorage(Address address) { }

    // BAL-backed mutations do not own MPT changes; CommitTree still delegates to commit the parent tree.
    public override void Commit(IReleaseSpec releaseSpec, IWorldStateTracer tracer, bool isGenesis = false, bool commitRoots = true) { }

    public override void DecrementNonce(Address address, ulong delta) { }

    public override void RecalculateStateRoot() { }

    public override void Reset(bool resetBlockChanges = true) { }

    public override ArrayPoolList<AddressAsKey> GetAccountChanges()
    {
        CheckInitialized();

        ReadOnlySpan<ReadOnlyAccountChanges> accounts = _suggestedBlockAccessList.AccountChanges.AsSpan();
        ArrayPoolList<AddressAsKey> result = new(accounts.Length);
        foreach (ReadOnlyAccountChanges accountChanges in accounts)
        {
            if (accountChanges.HasStateChanges)
            {
                result.Add(new AddressAsKey(accountChanges.Address));
            }
        }
        return result;
    }

    public override ReadOnlySpan<byte> GetTransientState(in StorageCell storageCell)
    {
        _transientScratch = _transientStorageProvider.Get(in storageCell);
        return StorageWord.ToStorageBytes(in _transientScratch, out _);
    }

    public override void SetTransientState(in StorageCell storageCell, byte[] newValue)
        => _transientStorageProvider.Set(in storageCell, StorageWord.FromStorageBytes(newValue));

    public override void ResetTransient()
        => _transientStorageProvider.Reset();

    [MemberNotNull(nameof(_suggestedBlockAccessList), nameof(_suggestedBlockHeader))]
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

    private ReadOnlyAccountChanges GetAccountChangesOrThrow(Address address)
    {
        Debug.Assert(_suggestedBlockAccessList is not null);
        ReadOnlyAccountChanges? accountChanges = _suggestedBlockAccessList.GetAccountChanges(address);
        if (accountChanges is null)
        {
            ThrowMissingAccount(address);
        }

        return accountChanges;
    }

    private (IWorldState ParentReader, ReadOnlyAccountChanges AccountChanges) ResolveContext(Address address)
    {
        CheckInitialized();
        return (GetParentReader(), GetAccountChangesOrThrow(address));
    }

    private bool TryGetDeclaredCode(in ValueHash256 codeHash, [NotNullWhen(true)] out byte[]? code)
    {
        code = null;

        if (_codeChangesByHash is { } codeChangesByHash
            && codeChangesByHash.TryGetValue(codeHash, out (uint Index, byte[] Code) entry)
            && entry.Index < _blockAccessIndex)
        {
            code = entry.Code;
            return true;
        }
        return false;
    }

    private Dictionary<ValueHash256, (uint Index, byte[] Code)>? BuildCodeChangesByHash()
    {
        if (_suggestedBlockAccessList is null)
        {
            return null;
        }

        // Built once per block; entries are immutable across the block. TryGetCodeByHash filters
        // by Index < _blockAccessIndex at lookup time so future-tx code stays invisible. The
        // dictionary itself is only allocated when at least one account declares a code change,
        // so most blocks (which rarely contain deployments) skip the per-block allocation.
        Dictionary<ValueHash256, (uint Index, byte[] Code)>? codeChangesByHash = null;
        foreach (ReadOnlyAccountChanges accountChanges in _suggestedBlockAccessList.AccountChanges)
        {
            ReadOnlySpan<CodeChange> codeChanges = accountChanges.CodeChanges;
            if (codeChanges.Length == 0) continue;
            codeChangesByHash ??= new(GenericEqualityComparer.GetOptimized<ValueHash256>());
            foreach (CodeChange codeChange in codeChanges)
            {
                if (!codeChangesByHash.TryGetValue(codeChange.CodeHash, out (uint Index, byte[] Code) existing)
                    || codeChange.Index < existing.Index)
                {
                    codeChangesByHash[codeChange.CodeHash] = (codeChange.Index, codeChange.Code);
                }
            }
        }

        return codeChangesByHash;
    }

    private static bool TryGetDeclaredSlotChanges(ReadOnlyAccountChanges accountChanges, UInt256 slot, out ReadOnlySlotChanges? slotChanges)
    {
        if (accountChanges.TryGetSlotChanges(slot, out slotChanges))
        {
            return true;
        }

        if (accountChanges.IsStorageRead(slot))
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
        => throw new InvalidBlockLevelAccessListException(_suggestedBlockHeader!, $"Suggested block-level access list missing account changes for {address} at index {_blockAccessIndex}.");

    [DoesNotReturn, StackTraceHidden]
    private void ThrowMissingStorage(in StorageCell storageCell)
        => throw new InvalidBlockLevelAccessListException(_suggestedBlockHeader!, $"Storage access for {storageCell.Address} not in block access list at index {_blockAccessIndex}.");
}
