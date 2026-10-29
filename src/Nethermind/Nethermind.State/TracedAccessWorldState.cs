// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
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
/// Records only while <see cref="SetGeneratingBlockAccessList"/> holds a recorder, so the decorator can sit
/// idle in a simulation stack; with none installed every member delegates to the decorated state.
/// <see cref="Clear"/>, <see cref="SetIndex"/> and <see cref="IncrementIndex"/> instead go through the
/// guarded <see cref="GeneratingBlockAccessList"/>, so a missed setup in block processing fails fast with
/// an <see cref="InvalidOperationException"/> naming it instead of emitting an empty BAL.
/// </remarks>
public class TracedAccessWorldState(IWorldState state, bool parallel) : WorldStateDecorator(state), IBlockAccessListSource
{
    private BlockAccessIndexRecorder? _generatingBlockAccessList;
    private int _systemAccountReadSuppressionDepth;
    private UInt256 _scratchBalance;
    private ValueHash256 _scratchCodeHash;
    // Single-slot cache for the last storage cell read: a repeated same-cell SLOAD skips the BAL
    // read-recording. Reset whenever the recorder, its index or its journal moves.
    private StorageCell _lastReadStorageCell;
    private bool _hasLastReadStorageCell;

    /// <summary>Marks the declared reads executed, which then record only their account.</summary>
    public BalReadCoverage? ReadCoverage { get; set; }

    public BlockAccessIndexRecorder? GetGeneratingBlockAccessList() => _generatingBlockAccessList;
    public void SetGeneratingBlockAccessList(BlockAccessIndexRecorder? bal)
    {
        // The cached cell belongs to the outgoing recorder, so it cannot survive the swap.
        _hasLastReadStorageCell = false;
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

        bool? currentlyExists = _generatingBlockAccessList.GetAccountExists(address) ?? AccountExistsCurrent(address);
        UInt256? currentBalance = GetBalanceCurrent(address);
        bool wasCreated = base.AddToBalanceAndCreateIfNotExists(address, balanceChange, spec, out oldBalance);
        oldBalance = currentBalance ?? oldBalance;
        wasCreated = currentlyExists.HasValue ? !currentlyExists.Value : wasCreated;

        UInt256 newBalance = oldBalance + balanceChange;
        if (!ShouldSuppressSystemUserZeroBalanceChange(address, in balanceChange))
        {
            _generatingBlockAccessList.RecordAccountExistence(address, true);
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

        // Already recorded this exact cell; skip the read-recording.
        if (!_hasLastReadStorageCell || !_lastReadStorageCell.Equals(storageCell))
        {
            RecordStorageRead(in storageCell);
        }

        GetInternal(in storageCell, out value);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void RecordStorageRead(in StorageCell storageCell)
    {
        if (ReadCoverage?.TryMark(in storageCell) == true) GeneratingBlockAccessList.AddAccountRead(storageCell.Address);
        // A slot the index also writes is listed as a change; sealing the log drops the read.
        else GeneratingBlockAccessList.AddStorageRead(in storageCell);
        _lastReadStorageCell = storageCell;
        _hasLastReadStorageCell = true;
    }

    public override void IncrementNonce(Address address, ulong delta, out ulong oldNonce)
    {
        if (_generatingBlockAccessList is null) { base.IncrementNonce(address, delta, out oldNonce); return; }

        ulong? currentNonce = GetNonceCurrent(address);
        base.IncrementNonce(address, delta, out oldNonce);
        oldNonce = currentNonce ?? oldNonce;
        _generatingBlockAccessList.AddNonceChange(address, oldNonce + delta);
    }

    public override void SubtractFromBalanceAndSetNonce(Address address, in UInt256 balanceChange, ulong nonce, IReleaseSpec spec)
    {
        if (_generatingBlockAccessList is null) State.SubtractFromBalanceAndSetNonce(address, in balanceChange, nonce, spec);
        else base.SubtractFromBalanceAndSetNonce(address, in balanceChange, nonce, spec);
    }

    public override void SetNonce(Address address, in ulong nonce)
    {
        if (_generatingBlockAccessList is null) { base.SetNonce(address, nonce); return; }

        // Deliberately no AddAccountRead: the probe belongs to the tracer, not the caller, and EIP-7928 lists
        // only actual changes.
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
            _generatingBlockAccessList.AddCodeChange(address, GetCodeInternal(address), code);
        }
        return base.InsertCode(address, codeHash, code, spec, isGenesis);
    }

    /// <remarks>
    /// Recorded like <see cref="InsertCode"/> so a parallel run reads the code within the transaction; the
    /// finalizing <see cref="DeleteAccount"/> drops the change, so the account has no code change in the list.
    /// </remarks>
    public override void InsertTransientCode(Address address, in ValueHash256 codeHash, ReadOnlyMemory<byte> code, IReleaseSpec spec)
    {
        _generatingBlockAccessList?.AddCodeChange(address, GetCodeInternal(address), code);
        base.InsertTransientCode(address, in codeHash, code, spec);
    }

    public override bool AdoptCode(Address address, in ValueHash256 codeHash, ReadOnlyMemory<byte> code, IReleaseSpec spec)
    {
        _generatingBlockAccessList?.AddAdoptedCodeChange(address, GetCodeInternal(address), code, in codeHash);
        return base.AdoptCode(address, in codeHash, code, spec);
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
        if (State.TryGetStorageBeforeClear(in storageCell, out UInt256 beforeClear))
            _generatingBlockAccessList.SeedPreTxStorage(in storageCell, in beforeClear);
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

        AddAccountRead(address);
        if (_generatingBlockAccessList.TryGetBalance(address, out _scratchBalance)) return ref _scratchBalance;
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

        AddAccountRead(address);
        if (_generatingBlockAccessList.TryGetCodeChange(address, out CodeChange codeChange))
        {
            _scratchCodeHash = codeChange.CodeHash;
            return ref _scratchCodeHash;
        }
        return ref base.GetCodeHash(address);
    }

    public override ReadOnlyMemory<byte> GetCode(Address address)
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

    /// <summary>The live recorder, for the control members that must not silently no-op without one.</summary>
    /// <remarks>Every recording member checks <see cref="_generatingBlockAccessList"/> for null and delegates
    /// to the decorated state instead; see the class remarks.</remarks>
    private BlockAccessIndexRecorder GeneratingBlockAccessList
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _generatingBlockAccessList ?? ThrowGeneratingBlockAccessListNotSet();
    }

    [DoesNotReturn, StackTraceHidden]
    private static BlockAccessIndexRecorder ThrowGeneratingBlockAccessListNotSet() =>
        throw new InvalidOperationException("Block access list tracing requires a generating block access list to be set.");

    /// <summary>Starts recording <paramref name="index"/> after whatever the recorder's log already holds.</summary>
    public void SetIndex(uint index)
    {
        GeneratingBlockAccessList.Begin(index);
        _systemAccountReadSuppressionDepth = 0;
        _hasLastReadStorageCell = false;
    }

    public void IncrementIndex() => SetIndex(GeneratingBlockAccessList.Index + 1);

    /// <summary>Drops everything recorded for the current index.</summary>
    public void Clear()
    {
        GeneratingBlockAccessList.Discard();
        _systemAccountReadSuppressionDepth = 0;
        _hasLastReadStorageCell = false;
    }

    BlockAccessIndexRecorder? IBlockAccessListSource.GeneratedBlockAccessList => _generatingBlockAccessList;

    public override void Restore(Snapshot snapshot)
    {
        // A revert can drop the last cell's write, so drop the single-slot cache.
        _hasLastReadStorageCell = false;
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
        => _generatingBlockAccessList?.TryGetBalance(address, out UInt256 balance) == true ? balance : null;

    private ulong GetNonceInternal(Address address)
        => GetNonceCurrent(address) ?? base.GetNonce(address);

    private ulong? GetNonceCurrent(Address address)
        => _generatingBlockAccessList?.TryGetNonce(address, out ulong nonce) == true ? nonce : null;

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
        codeChange = _generatingBlockAccessList?.TryGetCodeChange(address, out CodeChange change) == true ? change : null;
        return codeChange is not null;
    }

    private ReadOnlyMemory<byte> GetCodeInternal(Address address)
        => GetCodeCurrent(address) is { } code ? code : base.GetCode(address);

    private ValueHash256 GetCodeHashInternal(Address address)
        => GetCodeHashCurrent(address, out ValueHash256? hash) ? hash.Value : base.GetCodeHash(address);

    // The decorated state of a sequential run already holds this index's writes.
    private void GetInternal(in StorageCell storageCell, out UInt256 value)
    {
        if (parallel && _generatingBlockAccessList?.TryGetStorage(in storageCell, out value) == true) return;
        base.Get(in storageCell, out value);
    }

    private bool AccountExistsInternal(Address address)
        => AccountExistsCurrent(address) ?? base.AccountExists(address);

    private bool? AccountExistsCurrent(Address address)
    {
        if (GetNonceCurrent(address) is not null || GetBalanceCurrent(address) is not null)
        {
            // if nonce or balance is changed in this tx must exist (could have been created this tx)
            return true;
        }

        // EIP-7928: code-only modifications (e.g. EIP-7702 SetCode) also imply existence at this index.
        if (TryGetCodeChangeCurrent(address, out CodeChange? codeChange) && codeChange.Value.Code.Length > 0)
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
