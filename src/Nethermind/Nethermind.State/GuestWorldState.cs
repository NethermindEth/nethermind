// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
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

namespace Nethermind.State;

/// <summary>
/// A world state for executing one block at a time on a single thread, such as a zkVM guest validating a block
/// against its witness.
/// </summary>
/// <remarks>
/// <para>
/// Gives the same results as <see cref="WorldState"/> with far fewer lookups. Each account and each storage slot the
/// block touches has one entry, found by one map probe; every change goes to one undo journal, so a snapshot is a
/// journal position. An entry holds the values the state's rules need: the current value, the value at the start of
/// the transaction (for <see cref="GetOriginal"/> and <see cref="GetOriginalBalance"/>) and the value last written to
/// the trie (to choose what the next root computation writes).
/// </para>
/// <para>
/// Account changes keep <see cref="StateProvider"/>'s change kinds, so a commit settles each account as it does,
/// EIP-158 removals included. A storage clear does not visit the cleared slots: it gives the account a new clear epoch,
/// and a slot reads as zero once its epoch is older than its account's. A slot made under a clear that was then
/// reverted is newer than its account, and loads again from the trie.
/// </para>
/// <para>Does not trace: a commit asked to trace state or storage throws.</para>
/// </remarks>
public sealed partial class GuestWorldState : IWorldState
{
    private const byte HeadNone = 0;
    private const byte HeadTouch = 1;
    private const byte HeadUpdate = 2;
    private const byte HeadNew = 3;
    private const byte HeadDelete = 4;
    private const byte HeadRecreateEmpty = 5;

    private const int NoSlot = -1;

    private static readonly UInt256 _zero = UInt256.Zero;

    private readonly LocalMetrics _metrics = new();
    private IWorldStateScopeProvider.IScope? _scope;
    private readonly EventHandler<IWorldStateScopeProvider.AccountUpdated> _onAccountUpdated;

    // Open-addressed tables over the entries, probed linearly: a bucket holds an entry's index + 1, or 0 when empty.
    // A probe compares the hash the entry keeps before its key, which a map probe on an equal hash cannot skip.
    private int[] _accountBuckets = new int[2_048];
    private AccountEntry[] _accounts = new AccountEntry[1_024];
    private int _accountCount;

    private int[] _slotBuckets = new int[16_384];
    private SlotEntry[] _slots = new SlotEntry[8_192];
    private int _slotCount;
    private int _lastSlot;

    private readonly OptimizedDictionary<StorageCell, int> _transientIndex = [];
    private TransientEntry[] _transients = new TransientEntry[64];
    private int _transientCount;
    private int _transientGeneration;

    private Undo[] _journal = new Undo[1_024];
    private int _journalCount;

    // The accounts changed in the current commit window, settled by the next commit.
    private readonly List<int> _windowAccounts = [];
    // The accounts whose committed value or storage the next root computation may have to write.
    private readonly List<int> _accountsToFlush = [];
    private readonly List<int> _storageToFlush = [];
    private readonly List<int> _deferredDeletes = [];

    // Transaction starts within the commit window, as (journal position, id of the transaction before it).
    private readonly List<(int Position, int PreviousTxId)> _transactionStarts = [];
    private int _txId = 1;
    private int _lastTxId = 1;
    private int _windowId = 1;
    // Storage accesses are numbered, so a slot tells whether the commit window touched it.
    private int _accessTick;
    private int _windowStartTick;
    private int _lastClearEpoch;

    private Dictionary<ValueHash256, byte[]>? _insertedCode;
    private Dictionary<ValueHash256, byte[]>? _transientCode;

    // The code the last bytecode access checked: code resolution checks an account's code and then
    // loads the same code by hash, so that load is answered here rather than from the code store again.
    private ValueHash256 _checkedCodeHash;
    private ReadOnlyMemory<byte> _checkedCode;

    private Address? _lastAddress;
    private int _lastAccount;

    private bool _needsStateRootUpdate;

    public GuestWorldState(IWorldStateScopeProvider scopeProvider)
    {
        ScopeProvider = scopeProvider;
        _onAccountUpdated = (_, updated) => OnAccountUpdated(updated.Address, updated.Account);
    }

    public IWorldStateScopeProvider ScopeProvider { get; }

    public bool IsInScope => _scope is not null;

    private IWorldStateScopeProvider.IScope Scope => _scope ?? ThrowOutOfScope();

    [DoesNotReturn, StackTraceHidden]
    private static IWorldStateScopeProvider.IScope ThrowOutOfScope() =>
        throw new InvalidOperationException($"{nameof(IWorldState)} must only be used within scope");

    public Hash256 StateRoot
    {
        get
        {
            if (_needsStateRootUpdate) ThrowStateRootNeedsUpdate();
            return Scope.RootHash;

            [DoesNotReturn, StackTraceHidden]
            static void ThrowStateRootNeedsUpdate() => throw new InvalidOperationException("State root needs to be updated");
        }
    }

    #region Scope

    public bool TryBeginScope(BlockHeader? baseBlock, [NotNullWhen(true)] out IDisposable? scopeCloser) =>
        TryBeginScope(baseBlock, atTarget: false, out scopeCloser);

    public bool TryBeginScopeAtTarget(BlockHeader targetBlock, [NotNullWhen(true)] out IDisposable? scopeCloser)
    {
        ArgumentNullException.ThrowIfNull(targetBlock);
        return TryBeginScope(targetBlock, atTarget: true, out scopeCloser);
    }

    private bool TryBeginScope(BlockHeader? block, bool atTarget, [NotNullWhen(true)] out IDisposable? scopeCloser)
    {
        if (_scope is not null) throw new InvalidOperationException("Cannot create nested worldstate scope.");

        bool acquired = atTarget
            ? ScopeProvider.TryBeginScopeAtTarget(block!, _metrics, out IWorldStateScopeProvider.IScope? scope)
            : ScopeProvider.TryBeginScope(block, _metrics, out scope);
        if (!acquired)
        {
            scopeCloser = null;
            return false;
        }

        _scope = scope!;
        scopeCloser = new ScopeCloser(this);
        return true;
    }

    private sealed class ScopeCloser(GuestWorldState worldState) : IDisposable
    {
        private GuestWorldState? _worldState = worldState;

        public void Dispose()
        {
            GuestWorldState? owner = _worldState;
            _worldState = null;
            owner?.EndScope();
        }
    }

    private void EndScope()
    {
        try
        {
            if (_scope is not null)
            {
                Reset();
                _scope.Dispose();
            }
        }
        finally
        {
            _scope = null;
        }
    }

    public bool HasStateForBlock(BlockHeader? baseBlock) => ScopeProvider.HasRoot(baseBlock);

    public bool HasStateForTargetBlock(BlockHeader targetBlock)
    {
        ArgumentNullException.ThrowIfNull(targetBlock);
        return ScopeProvider.HasStateForTargetBlock(targetBlock);
    }

    public Task HintBal(ReadOnlyBlockAccessList bal) => Scope.HintBal(bal);

    /// <summary>Not supported: a Block Access List is applied only by parallel execution, which this state does not serve.</summary>
    public void ApplyBal(ReadOnlyBlockAccessList bal, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException($"{nameof(GuestWorldState)} executes blocks sequentially.");

    #endregion

    #region Accounts

    /// <summary>Finds the entry of <paramref name="address"/>, loading the account from the trie on first use.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int AccountOf(Address address)
    {
        Address? last = _lastAddress;
        if (ReferenceEquals(last, address)) return _lastAccount;
        return FindAccount(address);
    }

    private int FindAccount(Address address)
    {
        int hash = new AddressAsKey(address).GetHashCode();
        int[] buckets = _accountBuckets;
        int mask = buckets.Length - 1;
        int bucket = hash & mask;
        int account;
        while ((account = buckets[bucket] - 1) >= 0)
        {
            ref AccountEntry entry = ref _accounts[account];
            if (entry.Hash == hash && (ReferenceEquals(entry.Address, address) || entry.Address.Equals(address))) goto Found;
            bucket = (bucket + 1) & mask;
        }

        account = AddAccount(address, hash);
        if (_accountCount * 2 > buckets.Length) Rehash(ref _accountBuckets, _accounts, _accountCount);
        else buckets[bucket] = account + 1;

    Found:
        _lastAddress = address;
        _lastAccount = account;
        return account;
    }

    private static void Rehash(ref int[] buckets, AccountEntry[] entries, int count)
    {
        int[] grown = new int[buckets.Length * 2];
        int mask = grown.Length - 1;
        for (int i = 0; i < count; i++)
        {
            int bucket = entries[i].Hash & mask;
            while (grown[bucket] != 0) bucket = (bucket + 1) & mask;
            grown[bucket] = i + 1;
        }

        buckets = grown;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private int AddAccount(Address address, int hash)
    {
        Account? account = Scope.Get(address);
        int index = _accountCount;
        if (index == _accounts.Length) Array.Resize(ref _accounts, index * 2);
        ref AccountEntry entry = ref _accounts[index];
        entry.Address = address;
        entry.Hash = hash;
        entry.Current = account;
        entry.Committed = account;
        entry.Flushed = account;
        entry.FirstSlot = NoSlot;
        _accountCount = index + 1;
        return index;
    }

    /// <summary>Reads the account at <paramref name="index"/>, as <see cref="StateProvider"/> reads through its cache.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Account? Read(int index)
    {
        ref AccountEntry entry = ref _accounts[index];
        if (entry.WindowStamp != _windowId) EnterWindow(ref entry);
        // An existing account read with nothing changed in the window is the bottom of its change chain, which
        // decides how a later removal commits.
        else if (entry.Head == HeadNone && !entry.ReadExisting && entry.Current is not null) entry.ReadExisting = true;
        return entry.Current;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void EnterWindow(ref AccountEntry entry)
    {
        entry.WindowStamp = _windowId;
        entry.Head = HeadNone;
        entry.FirstIsNew = false;
        entry.ReadExisting = entry.Current is not null;
    }

    private Account? Read(Address address) => Read(AccountOf(address));

    /// <summary>Records a change of the account at <paramref name="index"/> to <paramref name="account"/>.</summary>
    private void Change(int index, Account? account, byte head)
    {
        ref AccountEntry entry = ref _accounts[index];
        if (entry.WindowStamp != _windowId)
        {
            entry.WindowStamp = _windowId;
            entry.Head = HeadNone;
            entry.FirstIsNew = false;
            entry.ReadExisting = false;
        }

        if (head == HeadTouch && entry.Head == HeadTouch) return;

        ref Undo undo = ref PushUndo(UndoKind.Account, index);
        undo.Account = entry.Current;
        undo.TxStart = entry.TxStart;
        undo.Stamp = entry.TxStamp;
        undo.Aux = entry.Head | (entry.FirstIsNew ? 0x100 : 0);

        if (entry.TxStamp != _txId)
        {
            entry.TxStart = entry.Current;
            entry.TxStamp = _txId;
        }

        if (entry.Head == HeadNone)
        {
            entry.FirstIsNew = head == HeadNew && !entry.ReadExisting;
            if (entry.ListedWindow != _windowId)
            {
                entry.ListedWindow = _windowId;
                _windowAccounts.Add(index);
            }
        }

        entry.Head = head;
        entry.Current = account;
        _needsStateRootUpdate = true;
    }

    public Account GetAccount(Address address) => Read(address) ?? Account.TotallyEmpty;

    public bool TryGetAccount(Address address, out AccountStruct account)
    {
        Account stateAccount = GetAccount(address);
        _accounts[AccountOf(address)].StorageSeen = true;
        account = new AccountStruct(stateAccount.Nonce, stateAccount.Balance, stateAccount.StorageRoot, stateAccount.CodeHash);
        return !account.IsTotallyEmpty;
    }

    public bool IsContract(Address address) => Read(address)?.IsContract ?? false;

    public bool HasCode(Address address) => Read(address)?.HasCode ?? false;

    public bool AccountExists(Address address) => Read(address) is not null;

    public bool IsDeadAccount(Address address) => Read(address)?.IsEmpty ?? true;

    public bool IsNonZeroAccount(Address address, out bool accountExists)
    {
        Account? account = Read(address);
        accountExists = account is not null;
        return account is not null && (account.IsContract || account.Nonce != 0);
    }

    public ulong GetNonce(Address address) => Read(address)?.Nonce ?? 0;

    public ref readonly UInt256 GetBalance(Address address)
    {
        Account? account = Read(address);
        return ref account is not null ? ref account.Balance : ref _zero;
    }

    public ref readonly ValueHash256 GetCodeHash(Address address)
    {
        Account? account = Read(address);
        return ref account is not null ? ref account.CodeHash.ValueHash256 : ref Keccak.OfAnEmptyString.ValueHash256;
    }

    public UInt256 GetOriginalBalance(Address address)
    {
        int index = AccountOf(address);
        ref AccountEntry entry = ref _accounts[index];
        Account? original = entry.TxStamp == _txId ? entry.TxStart : entry.Current;
        return original?.Balance ?? UInt256.Zero;
    }

    public void DeleteAccount(Address address) => Change(AccountOf(address), null, HeadDelete);

    public void CreateAccount(Address address, in UInt256 balance, in ulong nonce = default)
    {
        Account account = balance.IsZero && nonce == 0 ? Account.TotallyEmpty : new Account(nonce, balance);
        Change(AccountOf(address), account, HeadNew);
    }

    public void CreateAccountIfNotExists(Address address, in UInt256 balance, in ulong nonce = default)
    {
        if (!AccountExists(address)) CreateAccount(address, balance, nonce);
    }

    public void CreateEmptyAccountIfDeleted(Address address)
    {
        int index = AccountOf(address);
        ref AccountEntry entry = ref _accounts[index];
        if (entry.WindowStamp != _windowId || entry.Head == HeadNone) return;

        // Only accounts deleted or created empty are kept empty, not ones a change left empty.
        if (entry.Head == HeadDelete || entry.Head is HeadTouch or HeadNew && entry.Current!.IsEmpty)
            Change(index, Account.TotallyEmpty, HeadRecreateEmpty);
    }

    public void AddToBalance(Address address, in UInt256 balanceChange, IReleaseSpec spec, out UInt256 oldBalance) =>
        SetNewBalance(address, in balanceChange, spec, isSubtracting: false, out oldBalance);

    public void SubtractFromBalance(Address address, in UInt256 balanceChange, IReleaseSpec spec, out UInt256 oldBalance) =>
        SetNewBalance(address, in balanceChange, spec, isSubtracting: true, out oldBalance);

    public bool AddToBalanceAndCreateIfNotExists(Address address, in UInt256 balanceChange, IReleaseSpec spec, out UInt256 oldBalance)
    {
        if (AccountExists(address))
        {
            AddToBalance(address, in balanceChange, spec, out oldBalance);
            return false;
        }

        oldBalance = UInt256.Zero;
        CreateAccount(address, in balanceChange);
        return true;
    }

    private void SetNewBalance(Address address, in UInt256 balanceChange, IReleaseSpec spec, bool isSubtracting, out UInt256 oldBalance)
    {
        _needsStateRootUpdate = true;
        int index = AccountOf(address);

        if (balanceChange.IsZero)
        {
            // Subtracting zero touches nothing, not even a missing sender; adding zero touches an empty account.
            if (spec.IsEip158Enabled && !isSubtracting)
            {
                Account touched = Read(index) ?? ThrowNonExistingAccount();
                if (touched.IsEmpty) Touch(index, address, spec, isZero: true);
            }

            oldBalance = UInt256.Zero;
            return;
        }

        Account account = Read(index) ?? ThrowNonExistingAccount();
        if (isSubtracting && account.Balance < balanceChange) ThrowInsufficientBalance(address);

        oldBalance = account.Balance;
        UInt256 newBalance = isSubtracting ? account.Balance - balanceChange : account.Balance + balanceChange;
        Change(index, account.WithChangedBalance(newBalance), HeadUpdate);

        [DoesNotReturn, StackTraceHidden]
        static Account ThrowNonExistingAccount() => throw new InvalidOperationException("Updating balance of a non-existing account");

        [DoesNotReturn, StackTraceHidden]
        static void ThrowInsufficientBalance(Address address) => throw new InsufficientBalanceException(address);
    }

    private void Touch(int index, Address address, IReleaseSpec spec, bool isZero)
    {
        if (isZero && address == spec.Eip158IgnoredAccount) return;
        Change(index, _accounts[index].Current, HeadTouch);
    }

    public void IncrementNonce(Address address, ulong delta, out ulong oldNonce)
    {
        int index = AccountOf(address);
        Account account = Read(index) ?? ThrowNullAccount(address);
        oldNonce = account.Nonce;
        Change(index, account.WithChangedNonce(oldNonce + delta), HeadUpdate);
    }

    public void DecrementNonce(Address address, ulong delta)
    {
        int index = AccountOf(address);
        Account account = Read(index) ?? ThrowNullAccount(address);
        Change(index, account.WithChangedNonce(account.Nonce - delta), HeadUpdate);
    }

    public void SetNonce(Address address, in ulong nonce)
    {
        int index = AccountOf(address);
        Account account = Read(index) ?? ThrowNullAccount(address);
        Change(index, account.WithChangedNonce(nonce), HeadUpdate);
    }

    public void SubtractFromBalanceAndSetNonce(Address address, in UInt256 balanceChange, ulong nonce, IReleaseSpec spec)
    {
        if (balanceChange.IsZero)
        {
            SetNonce(address, nonce);
            return;
        }

        int index = AccountOf(address);
        Account account = Read(index) ?? ThrowNullAccount(address);
        if (account.Balance < balanceChange) throw new InsufficientBalanceException(address);

        Change(index, new Account(nonce, account.Balance - balanceChange, account.StorageRoot, account.CodeHash), HeadUpdate);
    }

    [DoesNotReturn, StackTraceHidden]
    private static Account ThrowNullAccount(Address address) =>
        throw new InvalidOperationException($"Account {address} is null when updating nonce");

    #endregion

    #region Code

    public bool InsertCode(Address address, in ValueHash256 codeHash, ReadOnlyMemory<byte> code, IReleaseSpec spec, bool isGenesis = false)
    {
        int index = AccountOf(address);
        Account account = Read(index) ?? ThrowNullCodeAccount(address);
        bool inserted = (_insertedCode ??= []).TryAdd(codeHash, code.AsArray());
        SetCodeHash(index, address, account, in codeHash, spec, isGenesis);
        return inserted;
    }

    public void InsertTransientCode(Address address, in ValueHash256 codeHash, ReadOnlyMemory<byte> code, IReleaseSpec spec)
    {
        int index = AccountOf(address);
        Account account = Read(index) ?? ThrowNullCodeAccount(address);
        if (codeHash != Keccak.OfAnEmptyString.ValueHash256) (_transientCode ??= []).TryAdd(codeHash, code.AsArray());
        SetCodeHash(index, address, account, in codeHash, spec, isGenesis: false);
    }

    public void DiscardTransientCode() => _transientCode?.Clear();

    private void SetCodeHash(int index, Address address, Account account, in ValueHash256 codeHash, IReleaseSpec spec, bool isGenesis)
    {
        if (account.CodeHash.ValueHash256 != codeHash)
        {
            Change(index, account.WithChangedCodeHash((Hash256)codeHash), HeadUpdate);
        }
        else if (spec.IsEip158Enabled && !isGenesis && account.IsEmpty)
        {
            Touch(index, address, spec, account.Balance.IsZero);
        }
    }

    [DoesNotReturn, StackTraceHidden]
    private static Account ThrowNullCodeAccount(Address address) =>
        throw new InvalidOperationException($"Account {address} is null when updating code hash");

    public ReadOnlyMemory<byte> GetCode(Address address)
    {
        Account? account = Read(address);
        return account is null ? Array.Empty<byte>() : GetCode(in account.CodeHash.ValueHash256);
    }

    /// <inheritdoc/>
    /// <remarks>Code is keyed by its own hash, so the code remembered by <see cref="RecordBytecodeAccess"/> cannot go stale.</remarks>
    public ReadOnlyMemory<byte> GetCode(in ValueHash256 codeHash) =>
        !_checkedCode.IsNull() && codeHash == _checkedCodeHash ? _checkedCode : LoadCode(in codeHash);

    private ReadOnlyMemory<byte> LoadCode(in ValueHash256 codeHash)
    {
        if (codeHash == Keccak.OfAnEmptyString.ValueHash256) return Array.Empty<byte>();
        if (_insertedCode is not null && _insertedCode.TryGetValue(codeHash, out byte[]? inserted)) return inserted;
        if (_transientCode is not null && _transientCode.TryGetValue(codeHash, out byte[]? transient)) return transient;

        ReadOnlyMemory<byte> code = Scope.CodeDb.GetCode(codeHash);
        return code.IsNull() ? ThrowMissingCode(in codeHash) : code;

        [DoesNotReturn, StackTraceHidden]
        static ReadOnlyMemory<byte> ThrowMissingCode(in ValueHash256 codeHash) =>
            throw new InvalidOperationException($"Code {codeHash} is missing from the database.");
    }

    /// <summary>Throws when a contract's bytecode is absent from the code store.</summary>
    /// <remarks>The state is a witness, so every bytecode access is checked against it.</remarks>
    [SkipLocalsInit]
    public void RecordBytecodeAccess(Address address)
    {
        if (!IsContract(address)) return;

        ValueHash256 codeHash = GetCodeHash(address);
        _checkedCode = LoadCode(in codeHash);
        _checkedCodeHash = codeHash;
    }

    #endregion

    #region Storage

    /// <summary>Finds the entry of <paramref name="cell"/>, loading the slot on first use.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int SlotOf(in StorageCell cell)
    {
        // A storage write reads the slot and its original first, so one cell is often looked up three times in a row.
        // The zkVM guest keeps a cell's hash in the cell, so taking it costs a load there.
        long hash = cell.GetHashCode64();
        int last = _lastSlot;
        if ((uint)last < (uint)_slotCount && IsCell(ref _slots[last], in cell, hash)) return last;

        int[] buckets = _slotBuckets;
        int mask = buckets.Length - 1;
        int bucket = (int)hash & mask;
        int index;
        while ((index = buckets[bucket] - 1) >= 0)
        {
            if (IsCell(ref _slots[index], in cell, hash)) goto Found;
            bucket = (bucket + 1) & mask;
        }

        index = AddSlot(in cell, hash);
        if (_slotCount * 2 > buckets.Length) Rehash(ref _slotBuckets, _slots, _slotCount);
        else buckets[bucket] = index + 1;

    Found:
        _lastSlot = index;
        return index;
    }

    /// <remarks>
    /// Compares the index as words, where the general compare tests alignment per word on a 32-bit guest. The address
    /// is mostly the same instance.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsCell(ref SlotEntry slot, in StorageCell cell, long hash) =>
        slot.Hash == hash
        && slot.Cell.Index.Equals(in cell.Index)
        && (ReferenceEquals(slot.Cell.Address, cell.Address) || slot.Cell.Address.Equals(cell.Address));

    private static void Rehash(ref int[] buckets, SlotEntry[] entries, int count)
    {
        int[] grown = new int[buckets.Length * 2];
        int mask = grown.Length - 1;
        for (int i = 0; i < count; i++)
        {
            int bucket = (int)entries[i].Hash & mask;
            while (grown[bucket] != 0) bucket = (bucket + 1) & mask;
            grown[bucket] = i + 1;
        }

        buckets = grown;
    }

    private bool TryFindSlot(in StorageCell cell, out int index)
    {
        long hash = cell.GetHashCode64();
        int[] buckets = _slotBuckets;
        int mask = buckets.Length - 1;
        for (int bucket = (int)hash & mask; (index = buckets[bucket] - 1) >= 0; bucket = (bucket + 1) & mask)
        {
            if (IsCell(ref _slots[index], in cell, hash)) return true;
        }

        return false;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private int AddSlot(in StorageCell cell, long hash)
    {
        int account = AccountOf(cell.Address);
        ref AccountEntry owner = ref _accounts[account];
        int index = _slotCount;
        if (index == _slots.Length) Array.Resize(ref _slots, index * 2);
        ref SlotEntry slot = ref _slots[index];
        slot.Cell = cell;
        slot.Hash = hash;
        slot.Account = account;
        slot.NextInAccount = owner.FirstSlot;
        owner.FirstSlot = index;
        owner.StorageSeen = true;
        _slotCount = index + 1;

        // A slot of storage cleared in the block reads zero with no trie behind it to load from.
        if (owner.ClearEpoch != 0)
        {
            slot.Epoch = owner.ClearEpoch;
            slot.Unloaded = true;
        }
        else
        {
            LoadSlot(ref slot, ref owner);
        }

        return index;
    }

    private void LoadSlot(ref SlotEntry slot, ref AccountEntry owner)
    {
        (owner.Storage ??= Scope.CreateStorageTree(owner.Address)).Get(in slot.Cell.Index, out UInt256 value);
        slot.Current = value;
        slot.Original = value;
        slot.Epoch = 0;
        slot.Unloaded = false;
    }

    /// <summary>Reads the current value of the slot at <paramref name="index"/>.</summary>
    /// <remarks>By reference, so a caller copies the value once; read it before the slot table can grow.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ref readonly UInt256 ReadSlot(int index)
    {
        ref SlotEntry slot = ref _slots[index];
        int clearEpoch = _accounts[slot.Account].ClearEpoch;
        if (slot.Epoch == clearEpoch) return ref slot.Current;
        return ref ReadStaleSlot(ref slot, clearEpoch);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private ref readonly UInt256 ReadStaleSlot(ref SlotEntry slot, int clearEpoch)
    {
        // Older than its account's clear: cleared since. Newer: made under a clear that was reverted.
        if (slot.Epoch < clearEpoch || clearEpoch != 0) return ref _zero;

        LoadSlot(ref slot, ref _accounts[slot.Account]);
        return ref slot.Current;
    }

    public void Get(in StorageCell storageCell, out UInt256 value)
    {
        int index = SlotOf(in storageCell);
        _slots[index].RoundTick = ++_accessTick;
        value = ReadSlot(index);
    }

    public void GetOriginal(in StorageCell storageCell, out UInt256 value)
    {
        int index = SlotOf(in storageCell);
        ref SlotEntry slot = ref _slots[index];
        slot.RoundTick = ++_accessTick;
        if (slot.TxStamp == _txId) value = slot.TxOriginal;
        else value = ReadSlot(index);
    }

    public void Set(in StorageCell storageCell, in UInt256 newValue) => SetSlot(SlotOf(in storageCell), in newValue);

    private void SetSlot(int index, in UInt256 newValue)
    {
        UInt256 current = ReadSlot(index);
        ref SlotEntry slot = ref _slots[index];

        ref Undo undo = ref PushUndo(UndoKind.Slot, index);
        undo.Value = slot.Current;
        undo.TxOriginal = slot.TxOriginal;
        undo.Stamp = slot.TxStamp;
        undo.Aux = slot.Epoch;

        if (slot.TxStamp != _txId)
        {
            slot.TxOriginal = current;
            slot.TxStamp = _txId;
        }

        ref AccountEntry owner = ref _accounts[slot.Account];
        slot.Current = newValue;
        slot.Epoch = owner.ClearEpoch;
        slot.RoundTick = ++_accessTick;
        MarkStorageToFlush(slot.Account, ref owner);
        _needsStateRootUpdate = true;
    }

    public void Set(in StorageCell storageCell, in UInt256 newValue, in UInt256 currentValue) => Set(in storageCell, in newValue);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void MarkStorageToFlush(int account, ref AccountEntry owner)
    {
        if (owner.StorageToFlush) return;
        owner.StorageToFlush = true;
        _storageToFlush.Add(account);
    }

    public void ClearStorage(Address address)
    {
        ClearTransientStorage(address);

        int account = AccountOf(address);
        ref AccountEntry owner = ref _accounts[account];
        // Storage the block has seen, or that may be in the trie, is cleared even when it now holds nothing,
        // as WorldState does: whether a clear happened is observable (EIP-7906).
        if (!owner.StorageSeen && owner.Current?.HasStorage != true && Scope.Get(address)?.HasStorage != true) return;
        owner.StorageSeen = true;

        ref Undo undo = ref PushUndo(UndoKind.Clear, account);
        undo.Aux = owner.ClearEpoch;
        undo.Stamp = owner.ClearTxStamp;
        undo.Extra = owner.TxStartClearEpoch;
        undo.Tick = _accessTick;

        // The slots the commit window read or wrote keep their values as originals; any other slot reads its
        // original as zero, as WorldState captures originals only for the slots of its round. A slot made under a
        // reverted clear was never loaded, so it has no original to keep.
        // Every slot also keeps the value the transaction started with, which EIP-7906 reads past the clear. That
        // value cannot change within the transaction, so a revert of the clear leaves the stamp right.
        for (int slot = owner.FirstSlot; slot != NoSlot; slot = _slots[slot].NextInAccount)
        {
            ref SlotEntry entry = ref _slots[slot];
            if (entry.Epoch > owner.ClearEpoch) continue;

            // A slot made under this transaction's clear was never loaded; its starting value is the trie's.
            if (entry.BeforeClearStamp != _txId && !(entry.Unloaded && owner.ClearTxStamp == _txId && owner.TxStartClearEpoch == 0))
            {
                entry.BeforeClear = entry.TxStamp == _txId ? entry.TxOriginal : ReadSlot(slot);
                entry.BeforeClearStamp = _txId;
            }

            if (entry.RoundTick > _windowStartTick && entry.TxStamp != _txId) StampTransactionOriginal(slot);
        }

        if (owner.ClearTxStamp != _txId)
        {
            owner.TxStartClearEpoch = owner.ClearEpoch;
            owner.ClearTxStamp = _txId;
        }

        owner.ClearEpoch = ++_lastClearEpoch;
        MarkStorageToFlush(account, ref owner);
        _needsStateRootUpdate = true;
    }

    private void StampTransactionOriginal(int index)
    {
        UInt256 current = ReadSlot(index);
        ref SlotEntry slot = ref _slots[index];
        ref Undo undo = ref PushUndo(UndoKind.Slot, index);
        undo.Value = slot.Current;
        undo.TxOriginal = slot.TxOriginal;
        undo.Stamp = slot.TxStamp;
        undo.Aux = slot.Epoch;

        slot.TxOriginal = current;
        slot.TxStamp = _txId;
        slot.Current = current;
        slot.Epoch = _accounts[slot.Account].ClearEpoch;
    }

    /// <remarks>Only valid where no revert follows, so the revertible clear serves it as well.</remarks>
    public void MarkStorageDestroyed(Address address) => ClearStorage(address);

    public bool TryGetStorageBeforeClear(in StorageCell storageCell, out UInt256 value)
    {
        int account = AccountOf(storageCell.Address);
        ref AccountEntry owner = ref _accounts[account];
        if (owner.ClearTxStamp != _txId || owner.ClearEpoch == owner.TxStartClearEpoch)
        {
            value = default;
            return false;
        }

        // A clear stamps every slot it finds; one it did not find was not loaded before it.
        if (TryFindSlot(in storageCell, out int index) && _slots[index].BeforeClearStamp == _txId)
        {
            value = _slots[index].BeforeClear;
            return true;
        }

        if (owner.TxStartClearEpoch != 0)
        {
            value = default;
            return true;
        }

        owner.StorageSeen = true;
        (owner.Storage ??= Scope.CreateStorageTree(owner.Address)).Get(in storageCell.Index, out value);
        return true;
    }

    #endregion

    #region Transient storage

    public void GetTransientState(in StorageCell storageCell, out UInt256 value)
    {
        if (_transientCount != 0 && _transientIndex.TryGetValue(storageCell, out int index))
        {
            value = _transients[index].Value;
            return;
        }

        value = default;
    }

    public void SetTransientState(in StorageCell storageCell, in UInt256 newValue)
    {
        if (newValue.IsZero)
        {
            // An absent cell already reads zero.
            if (_transientCount != 0 && _transientIndex.TryGetValue(storageCell, out int present) && !_transients[present].Value.IsZero)
                SetTransient(present, in newValue);
            return;
        }

        ref int index = ref _transientIndex.GetValueRefOrAddDefault(storageCell, out bool exists);
        if (!exists) index = AddTransient(in storageCell);
        else if (_transients[index].Value == newValue) return;

        SetTransient(index, in newValue);
    }

    private int AddTransient(in StorageCell storageCell)
    {
        int index = _transientCount;
        if (index == _transients.Length) Array.Resize(ref _transients, index * 2);
        _transients[index] = new TransientEntry { Cell = storageCell };
        _transientCount = index + 1;
        return index;
    }

    private void SetTransient(int index, in UInt256 value)
    {
        ref Undo undo = ref PushUndo(UndoKind.Transient, index);
        undo.Value = _transients[index].Value;
        undo.Stamp = _transientGeneration;
        _transients[index].Value = value;
    }

    private void ClearTransientStorage(Address address)
    {
        for (int i = 0; i < _transientCount; i++)
        {
            ref TransientEntry entry = ref _transients[i];
            if (!entry.Value.IsZero && entry.Cell.Address == address) SetTransient(i, UInt256.Zero);
        }
    }

    public void ResetTransient()
    {
        _transientIndex.Clear();
        _transientCount = 0;
        // Journal entries of the dropped storage no longer have entries to restore.
        _transientGeneration++;
    }

    #endregion

    #region Journal

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ref Undo PushUndo(UndoKind kind, int index)
    {
        int position = _journalCount;
        if (position == _journal.Length) Array.Resize(ref _journal, position * 2);
        _journalCount = position + 1;
        // Written whole: the journal is not cleared, and a restored snapshot leaves the entries above it.
        ref Undo undo = ref _journal[position];
        undo = new Undo { Kind = kind, Index = index };
        return ref undo;
    }

    public Snapshot TakeSnapshot(bool newTransactionStart = false)
    {
        int position = _journalCount - 1;
        if (newTransactionStart)
        {
            _transactionStarts.Add((position, _txId));
            _txId = ++_lastTxId;
        }

        return new Snapshot(new Snapshot.Storage(position, position), position);
    }

    public void Restore(Snapshot snapshot) => Restore(snapshot.StateSnapshot);

    private void Restore(int position)
    {
        if (position >= _journalCount)
        {
            throw new InvalidOperationException($"{nameof(GuestWorldState)} tried to restore snapshot {position} beyond current position {_journalCount - 1}");
        }

        Undo[] journal = _journal;
        for (int i = _journalCount - 1; i > position; i--)
        {
            ref Undo undo = ref journal[i];
            switch (undo.Kind)
            {
                case UndoKind.Account:
                    {
                        ref AccountEntry entry = ref _accounts[undo.Index];
                        entry.Current = undo.Account;
                        entry.TxStart = undo.TxStart;
                        entry.TxStamp = undo.Stamp;
                        entry.Head = (byte)undo.Aux;
                        entry.FirstIsNew = (undo.Aux & 0x100) != 0;
                        break;
                    }
                case UndoKind.Slot:
                    {
                        ref SlotEntry slot = ref _slots[undo.Index];
                        slot.Current = undo.Value;
                        slot.TxOriginal = undo.TxOriginal;
                        slot.TxStamp = undo.Stamp;
                        slot.Epoch = undo.Aux;
                        break;
                    }
                case UndoKind.Clear:
                    {
                        ref AccountEntry entry = ref _accounts[undo.Index];
                        entry.ClearEpoch = undo.Aux;
                        entry.ClearTxStamp = undo.Stamp;
                        entry.TxStartClearEpoch = undo.Extra;
                        // Reads made under the reverted clear no longer count for the window, as WorldState drops the
                        // originals it captured after a clear when it reverts it.
                        for (int slot = entry.FirstSlot; slot != NoSlot; slot = _slots[slot].NextInAccount)
                        {
                            if (_slots[slot].RoundTick > undo.Tick) _slots[slot].RoundTick = _windowStartTick;
                        }

                        break;
                    }
                case UndoKind.Transient:
                    if (undo.Stamp == _transientGeneration) _transients[undo.Index].Value = undo.Value;
                    break;
            }

            undo.Account = null;
            undo.TxStart = null;
        }

        _journalCount = position + 1;

        while (_transactionStarts.Count != 0 && _transactionStarts[^1].Position > position)
        {
            _txId = _transactionStarts[^1].PreviousTxId;
            _transactionStarts.RemoveAt(_transactionStarts.Count - 1);
        }
    }

    private void ClearJournal()
    {
        _journalCount = 0;
        _transactionStarts.Clear();
        _txId = ++_lastTxId;
    }

    #endregion

    #region Commit

    public void Commit(IReleaseSpec releaseSpec, IWorldStateTracer tracer, bool isGenesis = false, bool commitRoots = true)
    {
        if (tracer.IsTracingState || tracer.IsTracingStorage)
            throw new NotSupportedException($"{nameof(GuestWorldState)} does not trace state.");

        _ = Scope;
        ResetTransient();
        CommitAccounts(removeEmptyAccounts: releaseSpec.IsEip158Enabled && !isGenesis);
        ClearJournal();
        _transientCode?.Clear();

        if (commitRoots) FlushToTree();
    }

    /// <summary>Settles every account the window changed by its newest change, as <see cref="StateProvider"/> commits.</summary>
    private void CommitAccounts(bool removeEmptyAccounts)
    {
        foreach (int index in _windowAccounts)
        {
            ref AccountEntry entry = ref _accounts[index];
            byte head = entry.Head;
            if (head == HeadNone) continue;

            Account? committed = entry.Committed;
            switch (head)
            {
                case HeadTouch:
                case HeadUpdate:
                    committed = removeEmptyAccounts && entry.Current!.IsEmpty ? null : entry.Current;
                    break;
                case HeadNew:
                    if (!removeEmptyAccounts || !entry.Current!.IsEmpty) committed = entry.Current;
                    break;
                case HeadRecreateEmpty:
                    committed = entry.Current;
                    break;
                case HeadDelete:
                    // Only a change chain that starts with a creation proves the account was absent before the window.
                    if (!entry.FirstIsNew) committed = null;
                    break;
            }

            entry.Committed = committed;
            entry.Current = committed;
            entry.Head = HeadNone;
            if (!entry.AccountToFlush)
            {
                entry.AccountToFlush = true;
                _accountsToFlush.Add(index);
            }
        }

        _windowAccounts.Clear();
        _windowId++;
        _windowStartTick = _accessTick;
    }

    /// <summary>Writes every committed change to the trie, storage before accounts, as <see cref="WorldState"/> flushes.</summary>
    private void FlushToTree()
    {
        IWorldStateScopeProvider.IScope scope = Scope;
        using (IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch = scope.StartWriteBatch(_accountsToFlush.Count))
        {
            writeBatch.OnAccountUpdated += _onAccountUpdated;

            foreach (int account in _storageToFlush)
            {
                FlushStorage(writeBatch, account);
            }

            _storageToFlush.Clear();

            foreach (int index in _accountsToFlush)
            {
                ref AccountEntry entry = ref _accounts[index];
                entry.AccountToFlush = false;
                if (entry.Committed == entry.Flushed) continue;

                writeBatch.Set(entry.Address, entry.Committed);
                entry.Flushed = entry.Committed;
            }

            _accountsToFlush.Clear();
        }

        _needsStateRootUpdate = true;
    }

    private void FlushStorage(IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch, int account)
    {
        ref AccountEntry owner = ref _accounts[account];
        owner.StorageToFlush = false;
        // A removed account takes its storage with it; its slots stay pending should it come back.
        if (owner.Committed is null) return;

        int clearEpoch = owner.ClearEpoch;
        bool cleared = clearEpoch != 0;
        int writes = 0;
        for (int i = owner.FirstSlot; i != NoSlot; i = _slots[i].NextInAccount)
        {
            ref SlotEntry slot = ref _slots[i];
            if (slot.Epoch == clearEpoch && (cleared ? !slot.Current.IsZero : slot.Current != slot.Original)) writes++;
        }

        if (writes == 0 && !cleared) return;

        using (IWorldStateScopeProvider.IStorageWriteBatch storageBatch = writeBatch.CreateStorageWriteBatch(owner.Address, writes))
        {
            if (cleared) storageBatch.Clear();

            // Inserts and updates before deletes: a delete can collapse a branch, which resolves the surviving
            // sibling, so writing the rest first keeps the traversal to the nodes the witness was made with.
            for (int i = owner.FirstSlot; i != NoSlot; i = _slots[i].NextInAccount)
            {
                ref SlotEntry slot = ref _slots[i];
                if (slot.Epoch != clearEpoch || (cleared ? slot.Current.IsZero : slot.Current == slot.Original)) continue;

                if (slot.Current.IsZero) _deferredDeletes.Add(i);
                else storageBatch.Set(in slot.Cell.Index, in slot.Current);
            }

            foreach (int i in _deferredDeletes)
            {
                storageBatch.Set(in _slots[i].Cell.Index, in UInt256.Zero);
            }

            _deferredDeletes.Clear();
        }

        for (int i = owner.FirstSlot; i != NoSlot; i = _slots[i].NextInAccount)
        {
            ref SlotEntry slot = ref _slots[i];
            if (slot.Epoch == clearEpoch)
            {
                slot.Original = slot.Current;
                slot.Epoch = 0;
                slot.Unloaded = false;
            }
            else if (cleared)
            {
                slot.Current = default;
                slot.Original = default;
                slot.Epoch = 0;
                slot.Unloaded = false;
            }
        }

        owner.ClearEpoch = 0;
    }

    private void OnAccountUpdated(Address address, Account? account)
    {
        ref AccountEntry entry = ref _accounts[AccountOf(address)];
        entry.Current = account;
        entry.Committed = account;
        entry.Flushed = account;
    }

    public void RecalculateStateRoot()
    {
        Scope.UpdateRootHash();
        _needsStateRootUpdate = false;
    }

    public void CommitTree(ulong blockNumber)
    {
        IWorldStateScopeProvider.IScope scope = Scope;
        if (_needsStateRootUpdate) RecalculateStateRoot();
        scope.Commit(blockNumber);
    }

    public void Reset(bool resetBlockChanges = true)
    {
        Restore(Snapshot.EmptyPosition);
        ClearJournal();
        ResetTransient();
        _transientCode?.Clear();
        _windowAccounts.Clear();
        _windowId++;
        _windowStartTick = _accessTick;
        _needsStateRootUpdate = false;

        if (resetBlockChanges)
        {
            Array.Clear(_accountBuckets);
            Array.Clear(_accounts, 0, _accountCount);
            _accountCount = 0;
            Array.Clear(_slotBuckets);
            Array.Clear(_slots, 0, _slotCount);
            _slotCount = 0;
            _lastSlot = 0;
            _accountsToFlush.Clear();
            _storageToFlush.Clear();
            _insertedCode?.Clear();
            _lastAddress = null;
            _checkedCode = default;
        }
    }

    #endregion

    #region Hints

    public void WarmUp(AccessList? accessList, CancellationToken cancellationToken = default) { }

    public void WarmUp(Address address) { }

    public ArrayPoolList<AddressAsKey>? GetAccountChanges() => null;

    #endregion

    private enum UndoKind : byte
    {
        Account,
        Slot,
        Clear,
        Transient,
    }

    /// <summary>A journal entry: what one change overwrote.</summary>
    private struct Undo
    {
        public UndoKind Kind;
        public int Index;
        public int Stamp;
        public int Aux;
        public int Extra;
        public int Tick;
        public Account? Account;
        public Account? TxStart;
        public UInt256 Value;
        public UInt256 TxOriginal;
    }

    private struct AccountEntry
    {
        public Address Address;
        public int Hash;
        // Current is journaled; Committed is the value at the last commit, Flushed the one last written to the trie.
        public Account? Current;
        public Account? Committed;
        public Account? Flushed;
        // The value at the start of the transaction stamped by TxStamp.
        public Account? TxStart;
        public IWorldStateScopeProvider.IStorageTree? Storage;
        public int TxStamp;
        public int WindowStamp;
        public int ListedWindow;
        public int ClearEpoch;
        public int ClearTxStamp;
        public int TxStartClearEpoch;
        public int FirstSlot;
        public byte Head;
        public bool FirstIsNew;
        public bool ReadExisting;
        public bool AccountToFlush;
        public bool StorageToFlush;
        // Whether the block read, wrote or cleared the account's storage.
        public bool StorageSeen;
    }

    private struct SlotEntry
    {
        public StorageCell Cell;
        public long Hash;
        public int Account;
        public int NextInAccount;
        // Current is journaled with TxOriginal, TxStamp and Epoch; Original is the value last written to the trie.
        public UInt256 Current;
        public UInt256 Original;
        public UInt256 TxOriginal;
        public int TxStamp;
        public int Epoch;
        // The access tick of the slot's last read or write.
        public int RoundTick;
        // The value at the start of the transaction stamped by BeforeClearStamp, which cleared the slot.
        public UInt256 BeforeClear;
        public int BeforeClearStamp;
        // Made under a clear, with no trie value loaded.
        public bool Unloaded;
    }

    private struct TransientEntry
    {
        public StorageCell Cell;
        public UInt256 Value;
    }
}
