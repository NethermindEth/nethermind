// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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
/// Setup contract: <see cref="SetGeneratingBlockAccessList"/> must run with a non-null slice
/// before any state-mutating method. Hot-path mutators dereference
/// <c>_generatingBlockAccessList</c> without a null-check, so a missed setup fails fast with
/// <see cref="NullReferenceException"/> at the first write rather than silently corrupting BAL output.
/// </remarks>
public class TracedAccessWorldState(IWorldState state, bool parallel) : WorldStateDecorator(state), IPreBlockCaches, IBlockAccessListSource
{
    public PreBlockCaches Caches => (ScopeProvider as IPreBlockCaches)?.Caches
        ?? throw new InvalidOperationException($"{nameof(IPreBlockCaches)} is unavailable from the wrapped world state's scope provider.");
    public bool IsWarmWorldState => (ScopeProvider as IPreBlockCaches)?.IsWarmWorldState ?? false;

    // Set by SetGeneratingBlockAccessList; see class remarks.
    private BlockAccessListAtIndex? _generatingBlockAccessList;
    private int _systemAccountReadSuppressionDepth;
    private UInt256 _scratchBalance;
    private ValueHash256 _scratchCodeHash;
    // Scratch buffer for intra-tx SLOAD on the parallel path (see GetInternal). Per-worker —
    // the returned span is consumed by the EVM stack push before another GetInternal runs.
    private readonly byte[] _scratchStorage = new byte[32];
    // Single-slot cache for the last storage cell read: a repeated same-cell SLOAD skips the BAL
    // read-recording. Reset in Clear() and Restore() (a revert can un-record the cell's slot).
    private StorageCell _lastReadStorageCell;
    private AccountChangesAtIndex? _lastReadStorageChanges;
    private bool _hasLastReadCell;
    public BlockAccessListAtIndex? GetGeneratingBlockAccessList() => _generatingBlockAccessList;
    public void SetGeneratingBlockAccessList(BlockAccessListAtIndex? bal) => _generatingBlockAccessList = bal;

    public override void AddToBalance(Address address, in UInt256 balanceChange, IReleaseSpec spec, out UInt256 oldBalance)
    {
        UInt256? currentBalance = GetBalanceCurrent(address);
        base.AddToBalance(address, balanceChange, spec, out oldBalance);
        oldBalance = currentBalance ?? oldBalance;

        UInt256 newBalance = oldBalance + balanceChange;
        _generatingBlockAccessList.AddBalanceChange(address, oldBalance, newBalance);
    }

    public override bool AddToBalanceAndCreateIfNotExists(Address address, in UInt256 balanceChange, IReleaseSpec spec, out UInt256 oldBalance)
    {
        bool? currentlyExists = AccountExistsCurrent(address);
        UInt256? currentBalance = GetBalanceCurrent(address);
        bool res = base.AddToBalanceAndCreateIfNotExists(address, balanceChange, spec, out oldBalance);
        oldBalance = currentBalance ?? oldBalance;
        res = currentlyExists ?? res;

        UInt256 newBalance = oldBalance + balanceChange;
        _generatingBlockAccessList.AddBalanceChange(address, oldBalance, newBalance);

        return res;
    }

    public override IDisposable? BeginSystemAccountReadSuppression() => new SystemAccountReadSuppressionScope(this);

    public override ReadOnlySpan<byte> Get(in StorageCell storageCell)
    {
        AccountChangesAtIndex accountChanges;
        if (_hasLastReadCell && _lastReadStorageCell.Equals(storageCell))
        {
            // Already recorded this exact cell; reuse its entry and skip the read-recording.
            accountChanges = _lastReadStorageChanges!;
        }
        else
        {
            accountChanges = _generatingBlockAccessList.RecordStorageReadAndGet(storageCell.Address, storageCell.Index);
            _lastReadStorageCell = storageCell;
            _lastReadStorageChanges = accountChanges;
            _hasLastReadCell = true;
        }
        return GetInternal(accountChanges, in storageCell);
    }

    public override void IncrementNonce(Address address, ulong delta, out ulong oldNonce)
    {
        ulong? currentNonce = GetNonceCurrent(address);
        base.IncrementNonce(address, delta, out oldNonce);
        oldNonce = currentNonce ?? oldNonce;
        _generatingBlockAccessList.AddNonceChange(address, oldNonce + delta);
    }

    public override void SetNonce(Address address, in ulong nonce)
    {
        base.SetNonce(address, nonce);
        _generatingBlockAccessList.AddNonceChange(address, nonce);
    }

    public override bool InsertCode(Address address, in ValueHash256 codeHash, ReadOnlyMemory<byte> code, IReleaseSpec spec, bool isGenesis = false)
    {
        byte[] oldCode = GetCodeInternal(address) ?? [];
        _generatingBlockAccessList.AddCodeChange(address, oldCode, code);
        return base.InsertCode(address, codeHash, code, spec, isGenesis);
    }

    public override void Set(in StorageCell storageCell, byte[] newValue)
    {
        ReadOnlySpan<byte> oldValue = GetInternal(storageCell);
        _generatingBlockAccessList.AddStorageChange(storageCell, new(oldValue, true), new(newValue, true));
        base.Set(storageCell, newValue);
    }

    public override ref readonly UInt256 GetBalance(Address address)
    {
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
        AddAccountRead(address);
        return GetNonceInternal(address);
    }

    public override ref readonly ValueHash256 GetCodeHash(Address address)
    {
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
        AddAccountRead(address);
        return GetCodeInternal(address);
    }

    public override void SubtractFromBalance(Address address, in UInt256 balanceChange, IReleaseSpec spec, out UInt256 oldBalance)
    {
        oldBalance = 0;

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
        _generatingBlockAccessList.DeleteAccount(address, GetBalanceInternal(address));
        base.DeleteAccount(address);
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
        AddAccountRead(address);
        account = AccountExistsInternal(address) ? new(
            GetNonceInternal(address),
            GetBalanceInternal(address),
            Keccak.EmptyTreeHash, // never used
            GetCodeHashInternal(address)) : AccountStruct.TotallyEmpty;
        return !account.IsTotallyEmpty;
    }

    public override void AddAccountRead(Address address)
    {
        if (_systemAccountReadSuppressionDepth == 0 || address != Address.SystemUser)
        {
            _generatingBlockAccessList.AddAccountRead(address);
        }
    }

    /// <summary>Records the account read (honoring SystemUser suppression) and returns its change entry in one probe.</summary>
    private AccountChangesAtIndex? RecordReadAndGetChanges(Address address)
        // Suppressed SystemUser reads must not be recorded: use a non-mutating lookup.
        => _systemAccountReadSuppressionDepth != 0 && address == Address.SystemUser
            ? _generatingBlockAccessList.GetAccountChanges(address)
            : _generatingBlockAccessList.RecordReadAndGet(address);

    public void SetIndex(uint index)
        => _generatingBlockAccessList.Index = index;

    public void IncrementIndex()
        => _generatingBlockAccessList.Index++;

    public void Clear()
    {
        _generatingBlockAccessList.Clear();
        _systemAccountReadSuppressionDepth = 0;
        _hasLastReadCell = false;
        _lastReadStorageChanges = null;
    }

    BlockAccessListAtIndex? IBlockAccessListSource.GeneratedBlockAccessList => _generatingBlockAccessList;

    public override void Restore(Snapshot snapshot)
    {
        // A revert can un-record the last cell's slot, so drop the single-slot cache.
        _hasLastReadCell = false;
        _lastReadStorageChanges = null;
        _generatingBlockAccessList.Restore(snapshot.BlockAccessListSnapshot);
        base.Restore(snapshot);
    }

    public override Snapshot TakeSnapshot(bool newTransactionStart = false)
    {
        int blockAccessListSnapshot = _generatingBlockAccessList.TakeSnapshot();
        Snapshot snapshot = base.TakeSnapshot(newTransactionStart);
        return new(snapshot.StorageSnapshot, snapshot.StateSnapshot, blockAccessListSnapshot);
    }

    public override bool AccountExists(Address address)
    {
        AddAccountRead(address);
        return AccountExistsInternal(address);
    }

    public override bool IsContract(Address address)
    {
        AddAccountRead(address);
        return GetCodeHashInternal(address) != Keccak.OfAnEmptyString;
    }

    public override bool IsStorageEmpty(Address address)
    {
        AddAccountRead(address);
        return base.IsStorageEmpty(address);
    }

    public override bool IsDeadAccount(Address address)
    {
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
        ulong? currentNonce = GetNonceCurrent(address);
        base.DecrementNonce(address, delta);
        ulong oldNonce = currentNonce ?? (GetNonce(address) + delta);
        _generatingBlockAccessList.AddNonceChange(address, oldNonce - delta);
    }
    private UInt256 GetBalanceInternal(Address address)
        => GetBalanceCurrent(address) ?? base.GetBalance(address);

    private UInt256? GetBalanceCurrent(Address address)
    {
        AccountChangesAtIndex? accountChanges = _generatingBlockAccessList.GetAccountChanges(address);
        return accountChanges?.BalanceChange?.Value;
    }

    private ulong GetNonceInternal(Address address)
        => GetNonceCurrent(address) ?? base.GetNonce(address);

    private ulong? GetNonceCurrent(Address address)
    {
        AccountChangesAtIndex? accountChanges = _generatingBlockAccessList.GetAccountChanges(address);
        return accountChanges?.NonceChange?.Value;
    }

    private byte[]? GetCodeCurrent(Address address)
        => TryGetCodeChangeCurrent(address, out CodeChange? codeChange) ? codeChange.Value.Code : null;

    private bool GetCodeHashCurrent(Address address, [NotNullWhen(true)] out ValueHash256? hash)
    {
        hash = null;
        bool res = TryGetCodeChangeCurrent(address, out CodeChange? codeChange);
        if (res)
        {
            hash = codeChange.Value.CodeHash;
        }
        return res;
    }

    private bool TryGetCodeChangeCurrent(Address address, [NotNullWhen(true)] out CodeChange? codeChange)
    {
        AccountChangesAtIndex? accountChanges = _generatingBlockAccessList.GetAccountChanges(address);
        codeChange = accountChanges?.CodeChange;
        return codeChange is not null;
    }

    private byte[]? GetCodeInternal(Address address)
        => GetCodeCurrent(address) ?? base.GetCode(address);

    private ValueHash256 GetCodeHashInternal(Address address)
        => GetCodeHashCurrent(address, out ValueHash256? hash) ? hash.Value : base.GetCodeHash(address);

    private ReadOnlySpan<byte> GetInternal(in StorageCell storageCell)
        => GetInternal(parallel ? _generatingBlockAccessList.GetAccountChanges(storageCell.Address) : null, in storageCell);

    private ReadOnlySpan<byte> GetInternal(AccountChangesAtIndex? accountChanges, in StorageCell storageCell)
    {
        if (parallel && accountChanges?.TryGetStorageChange(storageCell.Index, out StorageChange? change) == true)
        {
            // Store the 32-byte word straight into _scratchStorage; the returned span outlives this
            // frame without allocating a new byte[32] per SLOAD.
            Unsafe.WriteUnaligned(ref MemoryMarshal.GetArrayDataReference(_scratchStorage), change.Value.Value);
            return _scratchStorage;
        }

        return base.Get(storageCell);
    }

    private bool AccountExistsInternal(Address address)
        => AccountExistsCurrent(address) ?? base.AccountExists(address);

    private bool? AccountExistsCurrent(Address address)
    {
        AccountChangesAtIndex? accountChanges = _generatingBlockAccessList.GetAccountChanges(address);
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
        if (!balance.IsZero)
        {
            _generatingBlockAccessList.AddBalanceChange(address, 0, balance);
        }
        if (nonce != 0)
        {
            _generatingBlockAccessList.AddNonceChange(address, nonce);
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
