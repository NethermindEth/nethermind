// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing.State;
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
public class TracedAccessWorldState(IWorldState innerWorldState, bool parallel) : IWorldState, IPreBlockCaches, IBlockAccessListSource
{
    public PreBlockCaches Caches => (_innerWorldState.ScopeProvider as IPreBlockCaches)?.Caches
        ?? throw new InvalidOperationException($"{nameof(IPreBlockCaches)} is unavailable from the wrapped world state's scope provider.");
    public bool IsWarmWorldState => (_innerWorldState.ScopeProvider as IPreBlockCaches)?.IsWarmWorldState ?? false;

    public bool IsInScope => _innerWorldState.IsInScope;
    public IWorldStateScopeProvider ScopeProvider => _innerWorldState.ScopeProvider;
    public Hash256 StateRoot => _innerWorldState.StateRoot;
    protected IWorldState _innerWorldState = innerWorldState;
    // Set by SetGeneratingBlockAccessList; see class remarks.
    private BlockAccessListAtIndex? _generatingBlockAccessList;
    private int _systemAccountReadSuppressionDepth;
    private UInt256 _scratchBalance;
    private ValueHash256 _scratchCodeHash;
    // Scratch buffer for intra-tx SLOAD on the parallel path (see GetInternal). Per-worker —
    // the returned span is consumed by the EVM stack push before another GetInternal runs.
    private readonly byte[] _scratchStorage = new byte[32];

    // Small N-entry warm-read cache: short-circuits a repeated read of any of the last N distinct storage
    // slots (the dominant SLOAD pattern — loops re-reading the same handful of slots) so it skips both the
    // BAL recording and the layered inner-world-state lookups. The first (cold) read still records into the
    // BAL; storage_reads is a set, so re-recording on warm reads is a no-op per EIP-7928 and cold-only
    // recording keeps the generated BAL byte-identical. The cache is linear-scanned (N is tiny, the arrays
    // stay CPU-cache resident) and entirely invalidated on any value-changing or boundary operation
    // (see InvalidateWarmRead), so an entry can only ever hold the slot's current committed-so-far value.
    // A null Address marks an unused/invalidated slot. Values are kept verbatim in a fixed buffer (max
    // 32 bytes each) so warm hits and cold inserts never allocate.
    private const int WarmReadCacheSize = 8;
    private struct WarmReadEntry
    {
        public Address? Address;
        public UInt256 Index;
        public int Length;
    }
    private readonly WarmReadEntry[] _warmReadCache = new WarmReadEntry[WarmReadCacheSize];
    private readonly byte[] _warmReadValues = new byte[WarmReadCacheSize * 32];
    private int _warmReadNext;
    public BlockAccessListAtIndex? GetGeneratingBlockAccessList() => _generatingBlockAccessList;
    public void SetGeneratingBlockAccessList(BlockAccessListAtIndex? bal) => _generatingBlockAccessList = bal;

    public bool HasStateForBlock(BlockHeader? baseBlock)
        => _innerWorldState.HasStateForBlock(baseBlock);

    public void WarmUp(AccessList? accessList)
        => _innerWorldState.WarmUp(accessList);

    public void WarmUp(Address address)
        => _innerWorldState.WarmUp(address);

    public void AddToBalance(Address address, in UInt256 balanceChange, IReleaseSpec spec, out UInt256 oldBalance)
    {
        UInt256? currentBalance = GetBalanceCurrent(address);
        _innerWorldState.AddToBalance(address, balanceChange, spec, out oldBalance);
        oldBalance = currentBalance ?? oldBalance;

        UInt256 newBalance = oldBalance + balanceChange;
        _generatingBlockAccessList.AddBalanceChange(address, oldBalance, newBalance);
    }

    public bool AddToBalanceAndCreateIfNotExists(Address address, in UInt256 balanceChange, IReleaseSpec spec, out UInt256 oldBalance)
    {
        bool? currentlyExists = AccountExistsCurrent(address);
        UInt256? currentBalance = GetBalanceCurrent(address);
        bool res = _innerWorldState.AddToBalanceAndCreateIfNotExists(address, balanceChange, spec, out oldBalance);
        oldBalance = currentBalance ?? oldBalance;
        res = currentlyExists ?? res;

        UInt256 newBalance = oldBalance + balanceChange;
        _generatingBlockAccessList.AddBalanceChange(address, oldBalance, newBalance);

        return res;
    }

    public IDisposable BeginScope(BlockHeader? baseBlock)
        => _innerWorldState.BeginScope(baseBlock);

    public Task HintBal(ReadOnlyBlockAccessList bal) => _innerWorldState.HintBal(bal);

    public IDisposable? BeginSystemAccountReadSuppression() => new SystemAccountReadSuppressionScope(this);

    public ReadOnlySpan<byte> Get(in StorageCell storageCell)
    {
        // Fast path: a repeated read of a recently-read slot, unchanged since. The cold read already
        // recorded it in the BAL (set semantics make re-recording a no-op), so we can return the cached
        // value without touching the BAL or the inner world-state layers.
        WarmReadEntry[] cache = _warmReadCache;
        for (int i = 0; i < cache.Length; i++)
        {
            ref WarmReadEntry entry = ref cache[i];
            if (entry.Address is not null
                && entry.Index.Equals(storageCell.Index)
                && storageCell.Address == entry.Address)
            {
                return _warmReadValues.AsSpan(i * 32, entry.Length);
            }
        }

        _generatingBlockAccessList.AddStorageRead(storageCell);
        ReadOnlySpan<byte> value = GetInternal(storageCell);

        // Cache for the next repeated read. Storage values are at most a 32-byte word; anything larger is
        // not cached (it cannot be a normal storage value).
        if (value.Length <= 32)
        {
            int slot = _warmReadNext;
            value.CopyTo(_warmReadValues.AsSpan(slot * 32, 32));
            ref WarmReadEntry entry = ref cache[slot];
            entry.Address = storageCell.Address;
            entry.Index = storageCell.Index;
            entry.Length = value.Length;
            _warmReadNext = (slot + 1) % cache.Length;
        }

        return value;
    }

    // Drops the warm-read cache. Called from every path that can change a slot's value (writes,
    // storage/account clears, snapshot restore) or move the BAL tx index, so the cache never returns a
    // stale value. Clearing the Address marks each slot unused; O(N) with N tiny and only on mutations.
    private void InvalidateWarmRead()
    {
        WarmReadEntry[] cache = _warmReadCache;
        for (int i = 0; i < cache.Length; i++)
        {
            cache[i].Address = null;
        }
        _warmReadNext = 0;
    }

    public ReadOnlySpan<byte> GetOriginal(in StorageCell storageCell)
        => _innerWorldState.GetOriginal(storageCell);

    public void IncrementNonce(Address address, UInt256 delta, out UInt256 oldNonce)
    {
        UInt256? currentNonce = GetNonceCurrent(address);
        _innerWorldState.IncrementNonce(address, delta, out oldNonce);
        oldNonce = currentNonce ?? oldNonce;
        _generatingBlockAccessList.AddNonceChange(address, (ulong)(oldNonce + delta));
    }

    public void SetNonce(Address address, in UInt256 nonce)
    {
        _innerWorldState.SetNonce(address, nonce);
        _generatingBlockAccessList.AddNonceChange(address, (ulong)nonce);
    }

    public bool InsertCode(Address address, in ValueHash256 codeHash, ReadOnlyMemory<byte> code, IReleaseSpec spec, bool isGenesis = false)
    {
        byte[] oldCode = GetCodeInternal(address) ?? [];
        _generatingBlockAccessList.AddCodeChange(address, oldCode, code);
        return _innerWorldState.InsertCode(address, codeHash, code, spec, isGenesis);
    }

    public void Set(in StorageCell storageCell, byte[] newValue)
    {
        ReadOnlySpan<byte> oldValue = GetInternal(storageCell);
        _generatingBlockAccessList.AddStorageChange(storageCell, new(oldValue, true), new(newValue, true));
        _innerWorldState.Set(storageCell, newValue);
        InvalidateWarmRead();
    }

    public ref readonly UInt256 GetBalance(Address address)
    {
        AddAccountRead(address);
        AccountChangesAtIndex? accountChanges = _generatingBlockAccessList.GetAccountChanges(address);
        if (accountChanges?.BalanceChange is { } bc)
        {
            _scratchBalance = bc.Value;
            return ref _scratchBalance;
        }
        return ref _innerWorldState.GetBalance(address);
    }

    public UInt256 GetNonce(Address address)
    {
        AddAccountRead(address);
        return GetNonceInternal(address);
    }

    public ref readonly ValueHash256 GetCodeHash(Address address)
    {
        AddAccountRead(address);
        AccountChangesAtIndex? accountChanges = _generatingBlockAccessList.GetAccountChanges(address);
        if (accountChanges?.CodeChange is { } cc)
        {
            _scratchCodeHash = cc.CodeHash;
            return ref _scratchCodeHash;
        }
        return ref _innerWorldState.GetCodeHash(address);
    }

    public byte[]? GetCode(Address address)
    {
        AddAccountRead(address);
        return GetCodeInternal(address);
    }

    public byte[]? GetCode(in ValueHash256 codeHash)
        => _innerWorldState.GetCode(codeHash);

    public void SubtractFromBalance(Address address, in UInt256 balanceChange, IReleaseSpec spec, out UInt256 oldBalance)
    {
        oldBalance = 0;

        if (address == Address.SystemUser && balanceChange.IsZero)
        {
            return;
        }

        UInt256? currentBalance = GetBalanceCurrent(address);
        _innerWorldState.SubtractFromBalance(address, balanceChange, spec, out oldBalance);
        oldBalance = currentBalance ?? oldBalance;

        UInt256 newBalance = oldBalance - balanceChange;
        _generatingBlockAccessList.AddBalanceChange(address, oldBalance, newBalance);
    }

    public void DeleteAccount(Address address)
    {
        _generatingBlockAccessList.DeleteAccount(address, GetBalanceInternal(address));
        _innerWorldState.DeleteAccount(address);
        InvalidateWarmRead();
    }

    public void CreateAccount(Address address, in UInt256 balance, in UInt256 nonce = default)
    {
        RecordCreateAccount(address, balance, nonce);
        _innerWorldState.CreateAccount(address, balance, nonce);
        InvalidateWarmRead();
    }

    public void CreateAccountIfNotExists(Address address, in UInt256 balance, in UInt256 nonce = default)
    {
        RecordCreateAccount(address, balance, nonce);
        _innerWorldState.CreateAccountIfNotExists(address, balance, nonce);
        InvalidateWarmRead();
    }

    public bool TryGetAccount(Address address, out AccountStruct account)
    {
        AddAccountRead(address);
        account = AccountExistsInternal(address) ? new(
            GetNonceInternal(address),
            GetBalanceInternal(address),
            Keccak.EmptyTreeHash, // never used
            GetCodeHashInternal(address)) : AccountStruct.TotallyEmpty;
        return !account.IsTotallyEmpty;
    }

    public void AddAccountRead(Address address)
    {
        if (_systemAccountReadSuppressionDepth == 0 || address != Address.SystemUser)
        {
            _generatingBlockAccessList.AddAccountRead(address);
        }
    }

    public void SetIndex(uint index)
    {
        _generatingBlockAccessList.Index = index;
        InvalidateWarmRead();
    }

    public void IncrementIndex()
    {
        _generatingBlockAccessList.Index++;
        InvalidateWarmRead();
    }

    public void Clear()
    {
        _generatingBlockAccessList.Clear();
        _systemAccountReadSuppressionDepth = 0;
        InvalidateWarmRead();
    }

    public void MergeGeneratingBal(GeneratedBlockAccessList target)
        => target.Merge(_generatingBlockAccessList);

    BlockAccessListAtIndex? IBlockAccessListSource.GeneratedBlockAccessList => _generatingBlockAccessList;

    public void Restore(Snapshot snapshot)
    {
        _generatingBlockAccessList.Restore(snapshot.BlockAccessListSnapshot);
        _innerWorldState.Restore(snapshot);
        InvalidateWarmRead();
    }

    public Snapshot TakeSnapshot(bool newTransactionStart = false)
    {
        int blockAccessListSnapshot = _generatingBlockAccessList.TakeSnapshot();
        Snapshot snapshot = _innerWorldState.TakeSnapshot(newTransactionStart);
        return new(snapshot.StorageSnapshot, snapshot.StateSnapshot, blockAccessListSnapshot);
    }

    public bool AccountExists(Address address)
    {
        AddAccountRead(address);
        return AccountExistsInternal(address);
    }

    public bool IsContract(Address address)
    {
        AddAccountRead(address);
        return GetCodeHashInternal(address) != Keccak.OfAnEmptyString;
    }

    public bool IsStorageEmpty(Address address)
    {
        AddAccountRead(address);
        return _innerWorldState.IsStorageEmpty(address);
    }

    public bool IsDeadAccount(Address address)
    {
        AddAccountRead(address);
        return !AccountExistsInternal(address) ||
            (
                GetBalanceInternal(address) == 0 &&
                GetNonceInternal(address) == 0 &&
                GetCodeHashInternal(address) == Keccak.OfAnEmptyString);
    }

    public void ClearStorage(Address address)
    {
        AddAccountRead(address);
        _innerWorldState.ClearStorage(address);
        InvalidateWarmRead();
    }

    public void Commit(IReleaseSpec releaseSpec, IWorldStateTracer tracer, bool isGenesis = false, bool commitRoots = true)
        => _innerWorldState.Commit(releaseSpec, tracer, isGenesis, commitRoots);

    public void CommitTree(long blockNumber)
        => _innerWorldState.CommitTree(blockNumber);

    public void DecrementNonce(Address address, UInt256 delta)
        => _innerWorldState.DecrementNonce(address, delta);

    public ArrayPoolList<AddressAsKey>? GetAccountChanges()
        => _innerWorldState.GetAccountChanges();

    public ReadOnlySpan<byte> GetTransientState(in StorageCell storageCell)
        => _innerWorldState.GetTransientState(storageCell);

    public void SetTransientState(in StorageCell storageCell, byte[] newValue)
        => _innerWorldState.SetTransientState(storageCell, newValue);

    public void RecalculateStateRoot()
        => _innerWorldState.RecalculateStateRoot();

    public void Reset(bool resetBlockChanges = true)
    {
        _innerWorldState.Reset(resetBlockChanges);
        InvalidateWarmRead();
    }

    public void ResetTransient()
        => _innerWorldState.ResetTransient();

    public void CreateEmptyAccountIfDeleted(Address address)
        => _innerWorldState.CreateEmptyAccountIfDeleted(address);

    private UInt256 GetBalanceInternal(Address address)
        => GetBalanceCurrent(address) ?? _innerWorldState.GetBalance(address);

    private UInt256? GetBalanceCurrent(Address address)
    {
        AccountChangesAtIndex? accountChanges = _generatingBlockAccessList.GetAccountChanges(address);
        return accountChanges?.BalanceChange?.Value;
    }

    private UInt256 GetNonceInternal(Address address)
        => GetNonceCurrent(address) ?? _innerWorldState.GetNonce(address);

    private UInt256? GetNonceCurrent(Address address)
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
        => GetCodeCurrent(address) ?? _innerWorldState.GetCode(address);

    private ValueHash256 GetCodeHashInternal(Address address)
        => GetCodeHashCurrent(address, out ValueHash256? hash) ? hash.Value : _innerWorldState.GetCodeHash(address);

    private ReadOnlySpan<byte> GetInternal(in StorageCell storageCell)
    {
        if (parallel)
        {
            AccountChangesAtIndex? accountChanges = _generatingBlockAccessList.GetAccountChanges(storageCell.Address);
            if (accountChanges is not null && accountChanges.TryGetStorageChange(storageCell.Index, out StorageChange? change))
            {
                // Copy the BE word into _scratchStorage so the returned span outlives this
                // frame without allocating a new byte[32] per SLOAD.
                EvmWord value = change.Value.Value;
                MemoryMarshal.CreateReadOnlySpan(
                    ref Unsafe.As<EvmWord, byte>(ref value), 32).CopyTo(_scratchStorage);
                return _scratchStorage;
            }
        }

        return _innerWorldState.Get(storageCell);
    }

    private bool AccountExistsInternal(Address address)
        => AccountExistsCurrent(address) ?? _innerWorldState.AccountExists(address);

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

    private void RecordCreateAccount(Address address, in UInt256 balance, in UInt256 nonce = default)
    {
        AddAccountRead(address);
        if (!balance.IsZero)
        {
            _generatingBlockAccessList.AddBalanceChange(address, 0, balance);
        }
        if (!nonce.IsZero)
        {
            _generatingBlockAccessList.AddNonceChange(address, (ulong)nonce);
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
