// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;
using Nethermind.Int256;

[assembly: InternalsVisibleTo("Nethermind.Evm.Test")]
[assembly: InternalsVisibleTo("Nethermind.State.Test")]

namespace Nethermind.State;

/// <remarks>
/// Records only while <see cref="SetGeneratingBlockAccessList"/> holds a slice, so the decorator can sit
/// idle in a simulation stack; with none installed every member delegates to the decorated state.
/// <see cref="Clear"/>, <see cref="SetIndex"/> and <see cref="IncrementIndex"/> instead go through the
/// guarded <see cref="GeneratingBlockAccessList"/>, so a missed setup in block processing fails fast with
/// an <see cref="InvalidOperationException"/> naming it instead of emitting an empty BAL.
/// </remarks>
public class TracedAccessWorldState(IWorldState state, bool parallel) : WorldStateDecorator(state), IBlockAccessListSource
{
    private BlockAccessListAtIndex? _generatingBlockAccessList;
    private int _systemAccountReadSuppressionDepth;
    private UInt256 _scratchBalance;
    private ValueHash256 _scratchCodeHash;
    // Single-slot cache for the last storage cell read: a repeated same-cell SLOAD skips the BAL
    // read-recording. Reset in Clear() and Restore() (a revert can un-record the cell's slot).
    private StorageCell _lastReadStorageCell;
    private AccountChangesAtIndex? _lastReadStorageChanges;

    /// <summary>Optional worker coverage replacing materialization of declared read-only slots.</summary>
    /// <remarks>Set only between execution slices, with the same coverage used by the BAL-backed state.</remarks>
    public BalReadCoverage? ReadCoverage { get; set; }

    public BlockAccessListAtIndex? GetGeneratingBlockAccessList() => _generatingBlockAccessList;
    public void SetGeneratingBlockAccessList(BlockAccessListAtIndex? bal)
    {
        // The cached entry belongs to the outgoing slice, so it cannot survive the swap.
        _lastReadStorageChanges = null;
        _generatingBlockAccessList = bal;
    }

    public override void AddToBalance(Address address, in UInt256 balanceChange, IReleaseSpec spec, out UInt256 oldBalance)
    {
        if (_generatingBlockAccessList is null) { base.AddToBalance(address, in balanceChange, spec, out oldBalance); return; }

        UInt256? currentBalance = GetBalanceCurrent(address);
        base.AddToBalance(address, balanceChange, spec, out oldBalance);
        oldBalance = currentBalance ?? oldBalance;

        UInt256 newBalance = oldBalance + balanceChange;
        if (!ShouldSuppressSystemUserZeroBalanceChange(address, in balanceChange))
        {
            _generatingBlockAccessList.AddBalanceChange(address, oldBalance, newBalance);
        }
    }

    public override bool AddToBalanceAndCreateIfNotExists(Address address, in UInt256 balanceChange, IReleaseSpec spec, out UInt256 oldBalance)
    {
        if (_generatingBlockAccessList is null) return base.AddToBalanceAndCreateIfNotExists(address, in balanceChange, spec, out oldBalance);

        // Single probe: a miss in GetAccountChanges is not memoized, so re-deriving existence and
        // balance from the address would be three full dictionary misses on first touch.
        AccountChangesAtIndex? accountChanges = _generatingBlockAccessList.GetAccountChanges(address);
        bool? currentlyExists = accountChanges?.AccountExists ?? AccountExistsCurrent(accountChanges);
        UInt256? currentBalance = accountChanges?.BalanceChange?.Value;
        bool wasCreated = base.AddToBalanceAndCreateIfNotExists(address, balanceChange, spec, out oldBalance);
        oldBalance = currentBalance ?? oldBalance;
        wasCreated = currentlyExists.HasValue ? !currentlyExists.Value : wasCreated;

        UInt256 newBalance = oldBalance + balanceChange;
        if (!ShouldSuppressSystemUserZeroBalanceChange(address, in balanceChange))
        {
            // Skip the GetOrAddAccountChanges probe when physical existence is already recorded.
            if (accountChanges?.AccountExists is not true) _generatingBlockAccessList.RecordAccountExistence(address, true);
            _generatingBlockAccessList.AddBalanceChange(address, oldBalance, newBalance);
        }

        return wasCreated;
    }

    public override IDisposable? BeginSystemAccountReadSuppression() => new SystemAccountReadSuppressionScope(this);

    public override void Get(in StorageCell storageCell, out UInt256 value)
    {
        if (_generatingBlockAccessList is null)
        {
            base.Get(in storageCell, out value);
            return;
        }

        // Already recorded this exact cell; reuse its entry and skip the read-recording.
        if (_lastReadStorageChanges is { } cached && _lastReadStorageCell.Equals(storageCell))
        {
            GetInternal(cached, in storageCell, out value);
            return;
        }

        GetStorageSlow(in storageCell, out value);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void GetStorageSlow(in StorageCell storageCell, out UInt256 value)
    {
        bool covered = ReadCoverage?.TryMark(storageCell) == true;
        AccountChangesAtIndex accountChanges = GeneratingBlockAccessList.RecordReadAndGet(storageCell.Address);
        ref StorageChange change = ref CollectionsMarshal.GetValueRefOrNullRef(accountChanges.StorageChanges, storageCell.Index);
        bool hasChange = !Unsafe.IsNullRef(ref change);
        if (!covered && !hasChange) accountChanges.AddStorageRead(in storageCell.Index);
        _lastReadStorageCell = storageCell;
        _lastReadStorageChanges = accountChanges;

        if (parallel && hasChange)
        {
            value = change.Value;
            return;
        }
        base.Get(in storageCell, out value);
    }

    public override void IncrementNonce(Address address, ulong delta, out ulong oldNonce)
    {
        if (_generatingBlockAccessList is null) { base.IncrementNonce(address, delta, out oldNonce); return; }

        ulong? currentNonce = GetNonceCurrent(address);
        base.IncrementNonce(address, delta, out oldNonce);
        oldNonce = currentNonce ?? oldNonce;
        _generatingBlockAccessList.AddNonceChange(address, oldNonce + delta);
    }

    public override void SetNonce(Address address, in ulong nonce)
    {
        if (_generatingBlockAccessList is null) { base.SetNonce(address, nonce); return; }

        // Deliberately no AddAccountRead: the probe belongs to the tracer, not the caller, and EIP-7928 lists
        // only actual changes. PredeployInstaller.Install is the sole caller that can write a no-op nonce.
        ulong oldNonce = GetNonceInternal(address);
        base.SetNonce(address, nonce);
        if (nonce != oldNonce)
        {
            _generatingBlockAccessList.AddNonceChange(address, nonce);
        }
    }

    public override bool InsertCode(Address address, in ValueHash256 codeHash, ReadOnlyMemory<byte> code, IReleaseSpec spec, bool isGenesis = false)
    {
        if (_generatingBlockAccessList is not null)
        {
            _generatingBlockAccessList.AddCodeChange(address, GetCodeInternal(address) ?? [], code);
        }
        return base.InsertCode(address, codeHash, code, spec, isGenesis);
    }

    public override void Set(in StorageCell storageCell, in UInt256 newValue)
    {
        if (_generatingBlockAccessList is null)
        {
            base.Set(in storageCell, in newValue);
            return;
        }

        GetInternal(in storageCell, out UInt256 oldValue);
        Set(in storageCell, in newValue, in oldValue);
    }

    public override void Set(in StorageCell storageCell, in UInt256 newValue, in UInt256 currentValue)
    {
        if (_generatingBlockAccessList is null)
        {
            base.Set(in storageCell, in newValue, in currentValue);
            return;
        }

        AssertCurrentStorageValue(in storageCell, in currentValue);
        _generatingBlockAccessList.AddStorageChange(in storageCell, in currentValue, in newValue);
        State.Set(in storageCell, in newValue, in currentValue);
    }

    [Conditional("DEBUG")]
    private void AssertCurrentStorageValue(in StorageCell cell, in UInt256 expected)
    {
        GetInternal(in cell, out UInt256 actual);
        Debug.Assert(actual == expected, "Storage must not change between reading the current value and recording the write.");
    }

    public override ref readonly UInt256 GetBalance(Address address)
    {
        if (_generatingBlockAccessList is null) return ref base.GetBalance(address);

        AccountChangesAtIndex? accountChanges = RecordReadAndGetChanges(address);
        if (accountChanges?.BalanceChange is { } bc)
        {
            _scratchBalance = bc.Value;
            return ref _scratchBalance;
        }
        return ref base.GetBalance(address);
    }

    public override ulong GetNonce(Address address)
    {
        if (_generatingBlockAccessList is null) return base.GetNonce(address);

        AddAccountRead(address);
        return GetNonceInternal(address);
    }

    public override ref readonly ValueHash256 GetCodeHash(Address address)
    {
        if (_generatingBlockAccessList is null) return ref base.GetCodeHash(address);

        AccountChangesAtIndex? accountChanges = RecordReadAndGetChanges(address);
        if (accountChanges?.CodeChange is { } cc)
        {
            _scratchCodeHash = cc.CodeHash;
            return ref _scratchCodeHash;
        }
        return ref base.GetCodeHash(address);
    }

    public override byte[]? GetCode(Address address)
    {
        if (_generatingBlockAccessList is null) return base.GetCode(address);

        AddAccountRead(address);
        return GetCodeInternal(address);
    }

    public override void SubtractFromBalance(Address address, in UInt256 balanceChange, IReleaseSpec spec, out UInt256 oldBalance)
    {
        if (_generatingBlockAccessList is null) { base.SubtractFromBalance(address, in balanceChange, spec, out oldBalance); return; }

        oldBalance = 0;

        // Intentionally not gated on read suppression: system transactions debit Address.SystemUser
        // as their sender, and that zero touch must stay out of BALs.
        if (address == Address.SystemUser && balanceChange.IsZero)
        {
            return;
        }

        UInt256? currentBalance = GetBalanceCurrent(address);
        base.SubtractFromBalance(address, balanceChange, spec, out oldBalance);
        oldBalance = currentBalance ?? oldBalance;

        UInt256 newBalance = oldBalance - balanceChange;
        _generatingBlockAccessList.AddBalanceChange(address, oldBalance, newBalance);
    }

    public override void DeleteAccount(Address address)
    {
        _generatingBlockAccessList?.DeleteAccount(address, GetBalanceInternal(address));
        base.DeleteAccount(address);
        _generatingBlockAccessList?.RecordAccountExistence(address, false);
    }

    public override void CreateAccount(Address address, in UInt256 balance, in ulong nonce = default)
    {
        RecordCreateAccount(address, balance, nonce);
        base.CreateAccount(address, balance, nonce);
    }

    public override void CreateAccountIfNotExists(Address address, in UInt256 balance, in ulong nonce = default)
    {
        RecordCreateAccount(address, balance, nonce);
        base.CreateAccountIfNotExists(address, balance, nonce);
    }

    public override bool TryGetAccount(Address address, out AccountStruct account)
    {
        if (_generatingBlockAccessList is null) return base.TryGetAccount(address, out account);

        AddAccountRead(address);
        account = AccountExistsInternal(address) ? new(
            GetNonceInternal(address),
            GetBalanceInternal(address),
            Keccak.EmptyTreeHash, // no caller on either the block or the simulation path reads it
            GetCodeHashInternal(address)) : AccountStruct.TotallyEmpty;
        return !account.IsTotallyEmpty;
    }

    public override void AddAccountRead(Address address)
    {
        if (_systemAccountReadSuppressionDepth == 0 || address != Address.SystemUser)
        {
            _generatingBlockAccessList?.AddAccountRead(address);
        }
    }

    private bool ShouldSuppressSystemUserZeroBalanceChange(Address address, in UInt256 balanceChange)
        => _systemAccountReadSuppressionDepth != 0 && address == Address.SystemUser && balanceChange.IsZero;

    /// <summary>Records physical existence, honoring SystemUser suppression.</summary>
    /// <remarks>
    /// Unlike a read, this creates the account's BAL entry, so an unguarded call would put a
    /// suppressed <see cref="Address.SystemUser"/> into the generated BAL and change its hash.
    /// </remarks>
    private void RecordAccountExistence(Address address, bool exists)
    {
        if (_systemAccountReadSuppressionDepth == 0 || address != Address.SystemUser)
        {
            _generatingBlockAccessList?.RecordAccountExistence(address, exists);
        }
    }

    /// <summary>Records the account read (honoring SystemUser suppression) and returns its change entry in one probe.</summary>
    /// <remarks>Only reached from members that have already established a live slice.</remarks>
    private AccountChangesAtIndex? RecordReadAndGetChanges(Address address)
        // Suppressed SystemUser reads must not be recorded: use a non-mutating lookup.
        => _systemAccountReadSuppressionDepth != 0 && address == Address.SystemUser
            ? _generatingBlockAccessList!.GetAccountChanges(address)
            : _generatingBlockAccessList!.RecordReadAndGet(address);

    /// <summary>The live slice, for the control members that must not silently no-op without one.</summary>
    /// <remarks>Every recording member checks <see cref="_generatingBlockAccessList"/> for null and delegates
    /// to the decorated state instead; see the class remarks.</remarks>
    private BlockAccessListAtIndex GeneratingBlockAccessList
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _generatingBlockAccessList ?? ThrowGeneratingBlockAccessListNotSet();
    }

    [DoesNotReturn, StackTraceHidden]
    private static BlockAccessListAtIndex ThrowGeneratingBlockAccessListNotSet() =>
        throw new InvalidOperationException("Block access list tracing requires a generating block access list to be set.");

    public void SetIndex(uint index)
        => GeneratingBlockAccessList.Index = index;

    public void IncrementIndex()
        => GeneratingBlockAccessList.Index++;

    public void Clear()
    {
        GeneratingBlockAccessList.Clear();
        _systemAccountReadSuppressionDepth = 0;
        _lastReadStorageChanges = null;
    }

    BlockAccessListAtIndex? IBlockAccessListSource.GeneratedBlockAccessList => _generatingBlockAccessList;

    public override void Restore(Snapshot snapshot)
    {
        // A revert can un-record the last cell's slot, so drop the single-slot cache.
        _lastReadStorageChanges = null;
        _generatingBlockAccessList?.Restore(snapshot.BlockAccessListSnapshot);
        base.Restore(snapshot);
    }

    public override Snapshot TakeSnapshot(bool newTransactionStart = false)
    {
        if (_generatingBlockAccessList is null) return base.TakeSnapshot(newTransactionStart);

        int blockAccessListSnapshot = _generatingBlockAccessList.TakeSnapshot();
        Snapshot snapshot = base.TakeSnapshot(newTransactionStart);
        return new(snapshot.StorageSnapshot, snapshot.StateSnapshot, blockAccessListSnapshot);
    }

    public override bool AccountExists(Address address)
    {
        if (_generatingBlockAccessList is null) return base.AccountExists(address);

        AddAccountRead(address);
        return AccountExistsInternal(address);
    }

    public override bool IsContract(Address address)
    {
        if (_generatingBlockAccessList is null) return base.IsContract(address);

        AddAccountRead(address);
        return GetCodeHashInternal(address) != Keccak.OfAnEmptyString;
    }

    public override bool IsDeadAccount(Address address)
    {
        if (_generatingBlockAccessList is null) return base.IsDeadAccount(address);

        AddAccountRead(address);
        return !AccountExistsInternal(address) ||
            (
                GetBalanceInternal(address) == 0 &&
                GetNonceInternal(address) == 0 &&
                GetCodeHashInternal(address) == Keccak.OfAnEmptyString);
    }

    public override void ClearStorage(Address address)
    {
        AddAccountRead(address);
        base.ClearStorage(address);
    }

    public override void DecrementNonce(Address address, ulong delta)
    {
        if (_generatingBlockAccessList is null) { base.DecrementNonce(address, delta); return; }

        ulong? currentNonce = GetNonceCurrent(address);
        base.DecrementNonce(address, delta);
        ulong oldNonce = currentNonce ?? (GetNonce(address) + delta);
        _generatingBlockAccessList.AddNonceChange(address, oldNonce - delta);
    }
    private UInt256 GetBalanceInternal(Address address)
        => GetBalanceCurrent(address) ?? base.GetBalance(address);

    private UInt256? GetBalanceCurrent(Address address)
    {
        AccountChangesAtIndex? accountChanges = _generatingBlockAccessList?.GetAccountChanges(address);
        return accountChanges?.BalanceChange?.Value;
    }

    private ulong GetNonceInternal(Address address)
        => GetNonceCurrent(address) ?? base.GetNonce(address);

    private ulong? GetNonceCurrent(Address address)
    {
        AccountChangesAtIndex? accountChanges = _generatingBlockAccessList?.GetAccountChanges(address);
        return accountChanges?.NonceChange?.Value;
    }

    private byte[]? GetCodeCurrent(Address address)
        => TryGetCodeChangeCurrent(address, out CodeChange? codeChange) ? codeChange.Value.Code : null;

    private bool GetCodeHashCurrent(Address address, [NotNullWhen(true)] out ValueHash256? hash)
    {
        if (TryGetCodeChangeCurrent(address, out CodeChange? codeChange))
        {
            hash = codeChange.Value.CodeHash;
            return true;
        }
        hash = null;
        return false;
    }

    private bool TryGetCodeChangeCurrent(Address address, [NotNullWhen(true)] out CodeChange? codeChange)
    {
        AccountChangesAtIndex? accountChanges = _generatingBlockAccessList?.GetAccountChanges(address);
        codeChange = accountChanges?.CodeChange;
        return codeChange is not null;
    }

    private byte[]? GetCodeInternal(Address address)
        => GetCodeCurrent(address) ?? base.GetCode(address);

    private ValueHash256 GetCodeHashInternal(Address address)
        => GetCodeHashCurrent(address, out ValueHash256? hash) ? hash.Value : base.GetCodeHash(address);

    private void GetInternal(in StorageCell storageCell, out UInt256 value)
        => GetInternal(parallel ? _generatingBlockAccessList?.GetAccountChanges(storageCell.Address) : null, in storageCell, out value);

    private void GetInternal(AccountChangesAtIndex? accountChanges, in StorageCell storageCell, out UInt256 value)
    {
        if (parallel && accountChanges is not null)
        {
            ref StorageChange change = ref CollectionsMarshal.GetValueRefOrNullRef(accountChanges.StorageChanges, storageCell.Index);
            if (!Unsafe.IsNullRef(ref change))
            {
                value = change.Value;
                return;
            }
        }

        base.Get(in storageCell, out value);
    }

    private bool AccountExistsInternal(Address address)
        => AccountExistsCurrent(address) ?? base.AccountExists(address);

    private bool? AccountExistsCurrent(Address address)
        => AccountExistsCurrent(_generatingBlockAccessList?.GetAccountChanges(address));

    private static bool? AccountExistsCurrent(AccountChangesAtIndex? accountChanges)
    {
        if (accountChanges is not null && (accountChanges.NonceChange is not null || accountChanges.BalanceChange is not null))
        {
            // if nonce or balance is changed in this tx must exist (could have been created this tx)
            return true;
        }

        // EIP-7928: code-only modifications (e.g. EIP-7702 SetCode) also imply existence at this index.
        if (accountChanges?.CodeChange is { Code.Length: > 0 })
        {
            return true;
        }

        return null;
    }

    private void RecordCreateAccount(Address address, in UInt256 balance, in ulong nonce = default)
    {
        AddAccountRead(address);
        RecordAccountExistence(address, true);
        if (!balance.IsZero)
        {
            _generatingBlockAccessList?.AddBalanceChange(address, 0, balance);
        }
        if (nonce != 0)
        {
            _generatingBlockAccessList?.AddNonceChange(address, nonce);
        }
    }

    private sealed class SystemAccountReadSuppressionScope : IDisposable
    {
        private TracedAccessWorldState? _stateProvider;

        public SystemAccountReadSuppressionScope(TracedAccessWorldState stateProvider)
        {
            _stateProvider = stateProvider;
            stateProvider._systemAccountReadSuppressionDepth++;
        }

        public void Dispose()
        {
            TracedAccessWorldState? stateProvider = _stateProvider;
            if (stateProvider is null)
            {
                return;
            }

            _stateProvider = null;
            stateProvider._systemAccountReadSuppressionDepth--;
        }
    }
}
