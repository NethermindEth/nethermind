// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.Tracing.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.State;

namespace Nethermind.Consensus.Processing;

/// <summary>The world state of a pre-warm env; while active it records the footprint of the transaction it runs.</summary>
/// <remarks>Re-implements <see cref="HasCode"/> and <see cref="MarkStorageDestroyed"/>, which the interface implements itself.</remarks>
internal sealed class FootprintRecorder(IWorldState state) : WorldStateDecorator(state), IWorldState
{
    private bool _active;
    private bool _opaque;

    private readonly Dictionary<AddressAsKey, int> _accountIndex = [];
    private AccountPrecondition[] _accounts = new AccountPrecondition[32];
    private int _accountCount;

    private readonly Dictionary<StorageCell, int> _slotIndex = [];
    private SlotPrecondition[] _slots = new SlotPrecondition[64];
    private int _slotCount;

    private StateEffect[] _effects = new StateEffect[32];
    // Per effect: the index of its account, or of its slot for a storage write.
    private int[] _owners = new int[32];
    private int _effectCount;

    private (Snapshot Snapshot, int Effects)[] _frames = new (Snapshot, int)[16];
    private int _frameCount;

    private BalanceMerge[] _merges = new BalanceMerge[32];
    private int[] _lastWrites = new int[64];
    private int _restoredWrites;

    private readonly StrongBox<ExecutionCounts> _counts = new();

    // Buffers a run grows past this are dropped at the next start, so a huge transaction is not kept for the env's life.
    private const int RetainedCapacity = 1024;

    public OutcomeTracer Outcome { get; } = new();

    public void Start(IBlockProcessingProgress progress, int txIndex, CancellationToken token)
    {
        _accountIndex.Clear();
        _accountCount = 0;
        _slotIndex.Clear();
        _slotCount = 0;
        Array.Clear(_effects, 0, _effectCount);
        _effectCount = 0;
        _frameCount = 0;
        _opaque = false;
        if (_accounts.Length > RetainedCapacity)
        {
            _accounts = new AccountPrecondition[32];
            _accountIndex.TrimExcess();
        }

        if (_slots.Length > RetainedCapacity)
        {
            _slots = new SlotPrecondition[64];
            _slotIndex.TrimExcess();
        }

        if (_effects.Length > RetainedCapacity)
        {
            _effects = new StateEffect[32];
            _owners = new int[32];
        }
        if (_merges.Length > RetainedCapacity) _merges = new BalanceMerge[32];
        if (_lastWrites.Length > RetainedCapacity) _lastWrites = new int[64];
        if (_frames.Length > RetainedCapacity) _frames = new (Snapshot, int)[16];
        Outcome.Reset(progress, txIndex, token);

        // As in block processing: empty transient storage, and originals (EIP-2200) taken at the transaction's start.
        State.ResetTransient();
        Snapshot start = State.TakeSnapshot(newTransactionStart: true);
        _active = true;
        PushFrame(in start);
        _counts.Value = default;
        Evm.Metrics.Capture(_counts);
    }

    public void Stop()
    {
        _active = false;
        Evm.Metrics.Capture(null);
    }

    /// <summary>Undoes a run that stopped part way.</summary>
    public void Discard()
    {
        _active = false;
        State.Restore(_frames[0].Snapshot);
        State.ResetTransient();
    }

    /// <param name="refreshed">Whether the run refreshes an invalidated footprint.</param>
    public TransactionFootprint? Finish(Transaction tx, in TransactionResult result, bool refreshed = false)
    {
        _active = false;
        if (_opaque || !result || !Outcome.HasResult) return null;

        // The run skipped the nonce check; only the transaction's own nonce makes it valid.
        if (!_accountIndex.TryGetValue(tx.SenderAddress!, out int senderIndex)) return null;
        ref readonly AccountPrecondition sender = ref _accounts[senderIndex];
        if ((sender.Fields & AccountFields.Nonce) == 0 || sender.Nonce != tx.Nonce || !sender.Exists) return null;

        // Before the accounts are copied: merging balance changes can raise an account's minimum balance.
        StateEffect[] effects = CompactEffects();

        int accountCount = 0;
        for (int i = 0; i < _accountCount; i++)
        {
            ref AccountPrecondition account = ref _accounts[i];
            if (account.BalanceValueReads == 0) account.Fields &= ~AccountFields.Balance;
            if (account.Fields != AccountFields.None) accountCount++;
        }

        AccountPrecondition[] accounts = accountCount == 0 ? [] : new AccountPrecondition[accountCount];
        for (int i = 0, j = 0; i < _accountCount; i++)
        {
            if (_accounts[i].Fields != AccountFields.None) accounts[j++] = _accounts[i];
        }

        int slotCount = 0;
        for (int i = 0; i < _slotCount; i++)
        {
            if (_slots[i].Read) slotCount++;
        }

        SlotPrecondition[] slots = slotCount == 0 ? [] : new SlotPrecondition[slotCount];
        for (int i = 0, j = 0; i < _slotCount; i++)
        {
            if (_slots[i].Read) slots[j++] = _slots[i];
        }

        FootprintReceipt receipt = new(Outcome.Success, Outcome.Recipient!, Outcome.Gas, Outcome.Logs, Outcome.Error);
        return new TransactionFootprint(tx, accounts, slots, effects, in receipt, in result, in _counts.Value) { Refreshed = refreshed, RestoredWrites = _restoredWrites };
    }

    /// <summary>The run's effects with fewer state calls to replay and the same state after a commit.</summary>
    /// <remarks>
    /// An account changed only by non-zero balance changes and nonce changes gets one net balance change. The commit
    /// keeps only an account's last value, and a touch commits as an update does. A minimum balance as large as the
    /// deepest deficit along the changes refuses a replay where a subtraction would have failed.
    /// A slot keeps only its last write, which overwrites the earlier ones whatever happens to the account between them.
    /// A zero-value debit changes nothing, and neither does a zero-value credit to an account that cannot be empty.
    /// </remarks>
    private StateEffect[] CompactEffects()
    {
        _restoredWrites = 0;
        int effectCount = _effectCount;
        if (effectCount == 0) return [];

        if (_merges.Length < _accountCount) _merges = new BalanceMerge[_accounts.Length];
        Span<BalanceMerge> merges = _merges.AsSpan(0, _accountCount);
        merges.Clear();
        if (_lastWrites.Length < _slotCount) _lastWrites = new int[_slots.Length];
        Span<int> lastWrites = _lastWrites.AsSpan(0, _slotCount);
        Span<StateEffect> effects = _effects.AsSpan(0, effectCount);
        ReadOnlySpan<int> owners = _owners.AsSpan(0, effectCount);

        for (int e = 0; e < effects.Length; e++)
        {
            ref readonly StateEffect effect = ref effects[e];
            if (effect.Kind == EffectKind.SetStorage)
            {
                lastWrites[owners[e]] = e;
                continue;
            }

            ref BalanceMerge merge = ref merges[owners[e]];
            switch (effect.Kind)
            {
                case EffectKind.AddToBalance:
                    if (effect.Value.IsZero) break;
                    merge.Changes++;
                    merge.Credit += effect.Value;
                    break;
                case EffectKind.SubtractFromBalance:
                    if (effect.Value.IsZero) break;
                    merge.Changes++;
                    merge.Debit += effect.Value;
                    if (merge.Debit > merge.Credit && merge.Debit - merge.Credit > merge.Deficit) merge.Deficit = merge.Debit - merge.Credit;
                    break;
                case EffectKind.IncrementNonce or EffectKind.DecrementNonce or EffectKind.SetNonce:
                    break;
                default:
                    // Creating or deleting the account sets its balance, which a net change moved past it would miss.
                    merge.Unmergeable = true;
                    break;
            }
        }

        int kept = 0;
        for (int e = 0; e < effects.Length; e++)
        {
            ref StateEffect effect = ref effects[e];
            if (effect.Kind == EffectKind.SetStorage)
            {
                int slot = owners[e];
                if (lastWrites[slot] != e) continue;
                if (effect.Value == _slots[slot].Value) _restoredWrites++;
            }
            else if (effect.Kind is EffectKind.AddToBalance or EffectKind.SubtractFromBalance && effect.Value.IsZero)
            {
                if (effect.Kind == EffectKind.SubtractFromBalance || KeepsCode(owners[e], merges)) continue;
            }
            else if (effect.Kind is EffectKind.AddToBalance or EffectKind.SubtractFromBalance)
            {
                int index = owners[e];
                ref BalanceMerge merge = ref merges[index];
                if (merge.Changes >= 2 && !merge.Unmergeable && merge.Credit != merge.Debit)
                {
                    // The first change carries the net one; the rest are dropped.
                    if (merge.Merged) continue;
                    merge.Merged = true;
                    bool credit = merge.Credit > merge.Debit;
                    effect.Kind = credit ? EffectKind.AddToBalance : EffectKind.SubtractFromBalance;
                    effect.Value = credit ? merge.Credit - merge.Debit : merge.Debit - merge.Credit;
                    if (!merge.Deficit.IsZero)
                    {
                        ref AccountPrecondition account = ref _accounts[index];
                        account.Fields |= AccountFields.MinimumBalance;
                        if (merge.Deficit > account.MinimumBalance) account.MinimumBalance = merge.Deficit;
                    }
                }
            }

            if (kept != e) effects[kept] = effect;
            kept++;
        }

        return effects[..kept].ToArray();
    }

    // An account the run requires code of, and does not create, delete or give code, is never empty (EIP-161), so a
    // zero-value credit touches nothing.
    private bool KeepsCode(int index, ReadOnlySpan<BalanceMerge> merges)
    {
        ref readonly AccountPrecondition account = ref _accounts[index];
        return (account.Fields & AccountFields.Code) != 0 && account.CodeHash != ValueKeccak.OfAnEmptyString && !merges[index].Unmergeable;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ref AccountPrecondition Account(Address address)
    {
        // Indexed after the index is taken: taking it can grow the array.
        int index = AccountIndex(address);
        return ref _accounts[index];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int AccountIndex(Address address)
    {
        ref int index = ref CollectionsMarshal.GetValueRefOrAddDefault(_accountIndex, address, out bool exists);
        if (!exists) index = AddAccount(address);
        return index;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private int AddAccount(Address address)
    {
        if (_accountCount == _accounts.Length) Array.Resize(ref _accounts, _accounts.Length * 2);
        int index = _accountCount++;
        ref AccountPrecondition account = ref _accounts[index];
        IWorldState state = State;
        account = default;
        account.Address = address;
        account.Exists = state.AccountExists(address);
        account.IsDead = state.IsDeadAccount(address);
        account.Nonce = state.GetNonce(address);
        account.Balance = state.GetBalance(address);
        account.CodeHash = state.GetCodeHash(address);
        return index;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ref AccountPrecondition Require(Address address, AccountFields fields)
    {
        ref AccountPrecondition account = ref Account(address);
        account.Fields |= fields;
        return ref account;
    }

    private ref SlotPrecondition Slot(in StorageCell cell, in UInt256 currentValue, bool currentKnown)
    {
        int index = SlotIndex(in cell, in currentValue, currentKnown);
        return ref _slots[index];
    }

    private int SlotIndex(in StorageCell cell, in UInt256 currentValue, bool currentKnown)
    {
        ref int index = ref CollectionsMarshal.GetValueRefOrAddDefault(_slotIndex, cell, out bool exists);
        if (!exists)
        {
            if (_slotCount == _slots.Length) Array.Resize(ref _slots, _slots.Length * 2);
            index = _slotCount++;
            ref SlotPrecondition slot = ref _slots[index];
            slot = default;
            slot.Cell = cell;
            if (currentKnown) slot.Value = currentValue;
            else State.Get(in cell, out slot.Value);
        }

        return index;
    }

    private ref StateEffect AddEffect(EffectKind kind, Address address, int owner)
    {
        if (_effectCount == _effects.Length)
        {
            Array.Resize(ref _effects, _effects.Length * 2);
            Array.Resize(ref _owners, _effects.Length);
        }

        _owners[_effectCount] = owner;
        ref StateEffect effect = ref _effects[_effectCount++];
        effect = default;
        effect.Kind = kind;
        effect.Address = address;
        return ref effect;
    }

    private ref StateEffect AccountEffect(EffectKind kind, Address address)
    {
        int owner = AccountIndex(address);
        _accounts[owner].Modified = true;
        return ref AddEffect(kind, address, owner);
    }

    private void PushFrame(in Snapshot snapshot)
    {
        if (_frameCount == _frames.Length) Array.Resize(ref _frames, _frames.Length * 2);
        _frames[_frameCount++] = (snapshot, _effectCount);
    }

    private static bool SamePositions(in Snapshot left, in Snapshot right) =>
        left.StateSnapshot == right.StateSnapshot
        && left.StorageSnapshot.PersistentStorageSnapshot == right.StorageSnapshot.PersistentStorageSnapshot
        && left.StorageSnapshot.TransientStorageSnapshot == right.StorageSnapshot.TransientStorageSnapshot
        && left.BlockAccessListSnapshot == right.BlockAccessListSnapshot;

    public override Snapshot TakeSnapshot(bool newTransactionStart = false)
    {
        Snapshot snapshot = base.TakeSnapshot(newTransactionStart);
        if (_active) PushFrame(in snapshot);
        return snapshot;
    }

    public override void Restore(Snapshot snapshot)
    {
        base.Restore(snapshot);
        if (!_active) return;

        int i = _frameCount - 1;
        while (i >= 0 && !SamePositions(in _frames[i].Snapshot, in snapshot)) i--;
        if (i < 0 || !IsUnambiguous(i))
        {
            _opaque = true;
            return;
        }

        _effectCount = _frames[i].Effects;
        _frameCount = i + 1;
    }

    /// <remarks>
    /// Snapshots with the same positions cannot tell which frame a revert undoes. That is harmless only when the
    /// effects between them are zero-value balance changes, which leave a live account as it is.
    /// </remarks>
    private bool IsUnambiguous(int index)
    {
        int first = index;
        while (first > 0 && SamePositions(in _frames[first - 1].Snapshot, in _frames[index].Snapshot)) first--;
        for (int e = _frames[first].Effects; e < _frames[index].Effects; e++)
        {
            ref readonly StateEffect effect = ref _effects[e];
            if (!effect.Value.IsZero
                || effect.Kind is not (EffectKind.AddToBalance or EffectKind.AddToBalanceAndCreateIfNotExists or EffectKind.SubtractFromBalance))
            {
                return false;
            }
        }

        return true;
    }

    public override bool TryGetAccount(Address address, out AccountStruct account)
    {
        // Exposes the storage root, which no precondition covers.
        if (_active) _opaque = true;
        return base.TryGetAccount(address, out account);
    }

    // An unmodified account must read its starting values; anything else is state the footprint does not describe.

    public override ulong GetNonce(Address address)
    {
        if (!_active) return base.GetNonce(address);
        ref AccountPrecondition account = ref Require(address, AccountFields.Nonce);
        ulong nonce = base.GetNonce(address);
        if (!account.Modified && account.Nonce != nonce) _opaque = true;
        return nonce;
    }

    public override ref readonly UInt256 GetBalance(Address address)
    {
        if (!_active) return ref base.GetBalance(address);
        ref AccountPrecondition account = ref Require(address, AccountFields.Balance);
        account.BalanceValueReads++;
        ref readonly UInt256 balance = ref base.GetBalance(address);
        if (!account.Modified && account.Balance != balance) _opaque = true;
        return ref balance;
    }

    public override ref readonly ValueHash256 GetCodeHash(Address address)
    {
        if (!_active) return ref base.GetCodeHash(address);
        ref AccountPrecondition account = ref Require(address, AccountFields.Code);
        ref readonly ValueHash256 codeHash = ref base.GetCodeHash(address);
        if (!account.Modified && account.CodeHash != codeHash) _opaque = true;
        return ref codeHash;
    }

    public override ReadOnlyMemory<byte> GetCode(Address address)
    {
        if (_active) Require(address, AccountFields.Code);
        return base.GetCode(address);
    }

    public override bool IsContract(Address address)
    {
        if (_active) Require(address, AccountFields.Code);
        return base.IsContract(address);
    }

    public bool HasCode(Address address)
    {
        if (_active) Require(address, AccountFields.Code);
        return base.IsContract(address);
    }

    public override bool AccountExists(Address address)
    {
        if (!_active) return base.AccountExists(address);
        ref AccountPrecondition account = ref Require(address, AccountFields.Existence);
        bool exists = base.AccountExists(address);
        if (!account.Modified && account.Exists != exists) _opaque = true;
        return exists;
    }

    public override bool IsDeadAccount(Address address)
    {
        if (!_active) return base.IsDeadAccount(address);
        ref AccountPrecondition account = ref Require(address, AccountFields.Liveness);
        bool isDead = base.IsDeadAccount(address);
        if (!account.Modified && account.IsDead != isDead) _opaque = true;
        return isDead;
    }

    public override void NoteMinimumBalance(Address address, in UInt256 minimum)
    {
        if (!_active) return;
        // The balance just read was only compared with the minimum. Translate it to the transaction's starting balance,
        // net of what the run itself paid or received, so a change made by an earlier transaction is still met.
        ref AccountPrecondition account = ref Account(address);
        if (account.BalanceValueReads > 0) account.BalanceValueReads--;
        UInt256 current = State.GetBalance(address);
        UInt256 atStart;
        if (current >= account.Balance)
        {
            UInt256 received = current - account.Balance;
            atStart = minimum > received ? minimum - received : UInt256.Zero;
        }
        else if (UInt256.AddOverflow(minimum, account.Balance - current, out atStart))
        {
            account.BalanceValueReads++;
            return;
        }

        account.Fields |= AccountFields.MinimumBalance;
        if (atStart > account.MinimumBalance) account.MinimumBalance = atStart;
    }

    public override void GetOriginal(in StorageCell storageCell, out UInt256 value)
    {
        base.GetOriginal(in storageCell, out value);
        if (!_active) return;
        ref SlotPrecondition slot = ref Slot(in storageCell, default, currentKnown: false);
        if (slot.Value != value) _opaque = true;
        slot.Read = true;
    }

    public override void Get(in StorageCell storageCell, out UInt256 value)
    {
        base.Get(in storageCell, out value);
        if (!_active) return;
        ref SlotPrecondition slot = ref Slot(in storageCell, in value, currentKnown: true);
        if (!slot.Written && slot.Value != value) _opaque = true;
        slot.Read = true;
    }

    public override void Set(in StorageCell storageCell, in UInt256 newValue)
    {
        if (_active) RecordWrite(in storageCell, in newValue);
        base.Set(in storageCell, in newValue);
    }

    public override void Set(in StorageCell storageCell, in UInt256 newValue, in UInt256 currentValue)
    {
        if (_active) RecordWrite(in storageCell, in newValue);
        // The base forwards to the two-argument write, which would record it again.
        State.Set(in storageCell, in newValue, in currentValue);
    }

    private void RecordWrite(in StorageCell storageCell, in UInt256 newValue)
    {
        int slot = SlotIndex(in storageCell, default, currentKnown: false);
        _slots[slot].Written = true;
        ref StateEffect effect = ref AddEffect(EffectKind.SetStorage, storageCell.Address, slot);
        effect.Index = storageCell.Index;
        effect.Value = newValue;
    }

    public override void ClearStorage(Address address)
    {
        if (_active) AccountEffect(EffectKind.ClearStorage, address);
        base.ClearStorage(address);
    }

    public void MarkStorageDestroyed(Address address)
    {
        if (_active) AccountEffect(EffectKind.MarkStorageDestroyed, address);
        State.MarkStorageDestroyed(address);
    }

    public override void DeleteAccount(Address address)
    {
        if (_active) AccountEffect(EffectKind.DeleteAccount, address);
        base.DeleteAccount(address);
    }

    public override void CreateAccount(Address address, in UInt256 balance, in ulong nonce = default)
    {
        if (_active)
        {
            ref StateEffect effect = ref AccountEffect(EffectKind.CreateAccount, address);
            effect.Value = balance;
            effect.Nonce = nonce;
        }

        base.CreateAccount(address, in balance, in nonce);
    }

    public override void CreateAccountIfNotExists(Address address, in UInt256 balance, in ulong nonce = default)
    {
        if (_active)
        {
            ref StateEffect effect = ref AccountEffect(EffectKind.CreateAccountIfNotExists, address);
            effect.Value = balance;
            effect.Nonce = nonce;
        }

        base.CreateAccountIfNotExists(address, in balance, in nonce);
    }

    public override void CreateEmptyAccountIfDeleted(Address address)
    {
        if (_active) _opaque = true;
        base.CreateEmptyAccountIfDeleted(address);
    }

    public override bool InsertCode(Address address, in ValueHash256 codeHash, ReadOnlyMemory<byte> code, IReleaseSpec spec, bool isGenesis = false)
    {
        if (_active)
        {
            ref StateEffect effect = ref AccountEffect(EffectKind.InsertCode, address);
            effect.CodeHash = codeHash;
            // Copied: the run's buffer may be reused.
            effect.Code = code.ToArray();
        }

        return base.InsertCode(address, in codeHash, code, spec, isGenesis);
    }

    public override void AddToBalance(Address address, in UInt256 balanceChange, IReleaseSpec spec, out UInt256 oldBalance)
    {
        if (_active) AccountEffect(EffectKind.AddToBalance, address).Value = balanceChange;
        base.AddToBalance(address, in balanceChange, spec, out oldBalance);
    }

    public override bool AddToBalanceAndCreateIfNotExists(Address address, in UInt256 balanceChange, IReleaseSpec spec, out UInt256 oldBalance)
    {
        if (_active)
        {
            // Only a precompile call branches on whether the account was created (the EIP-161 RIPEMD-160 touch).
            if (spec.IsPrecompile(address)) Require(address, AccountFields.Existence);
            AccountEffect(EffectKind.AddToBalanceAndCreateIfNotExists, address).Value = balanceChange;
        }

        return base.AddToBalanceAndCreateIfNotExists(address, in balanceChange, spec, out oldBalance);
    }

    public override void SubtractFromBalance(Address address, in UInt256 balanceChange, IReleaseSpec spec, out UInt256 oldBalance)
    {
        if (_active) AccountEffect(EffectKind.SubtractFromBalance, address).Value = balanceChange;
        base.SubtractFromBalance(address, in balanceChange, spec, out oldBalance);
    }

    public override void IncrementNonce(Address address, ulong delta, out ulong oldNonce)
    {
        if (_active) AccountEffect(EffectKind.IncrementNonce, address).Nonce = delta;
        base.IncrementNonce(address, delta, out oldNonce);
    }

    public override void DecrementNonce(Address address, ulong delta)
    {
        if (_active) AccountEffect(EffectKind.DecrementNonce, address).Nonce = delta;
        base.DecrementNonce(address, delta);
    }

    public override void SetNonce(Address address, in ulong nonce)
    {
        if (_active) AccountEffect(EffectKind.SetNonce, address).Nonce = nonce;
        base.SetNonce(address, in nonce);
    }

    public override void Reset(bool resetBlockChanges = true)
    {
        if (_active) _opaque = true;
        base.Reset(resetBlockChanges);
    }

    public override void Commit(IReleaseSpec releaseSpec, IWorldStateTracer tracer, bool isGenesis = false, bool commitRoots = true)
    {
        if (_active) _opaque = true;
        base.Commit(releaseSpec, tracer, isGenesis, commitRoots);
    }

    public override bool TryApplyAccountOverlay(IStateReadOverlay overlay)
    {
        if (_active) _opaque = true;
        return base.TryApplyAccountOverlay(overlay);
    }

    // One account's balance changes, while the run's effects are compacted.
    private struct BalanceMerge
    {
        public UInt256 Credit;
        public UInt256 Debit;
        public UInt256 Deficit;
        public int Changes;
        public bool Unmergeable;
        public bool Merged;
    }

    internal sealed class OutcomeTracer : TxTracer, ITxTracer
    {
        private IBlockProcessingProgress? _progress;
        private int _txIndex;
        private CancellationToken _token;

        public OutcomeTracer() => IsTracingReceipt = true;

        public bool IsCancelable => true;

        // Block processing executes a transaction it reaches before the run ends.
        public bool IsCancelled => _token.IsCancellationRequested || (_progress is not null && _progress.MainThreadTxIndex >= _txIndex);

        public bool HasResult { get; private set; }
        public bool Success { get; private set; }
        public Address? Recipient { get; private set; }
        public GasConsumed Gas { get; private set; }
        public LogEntry[] Logs { get; private set; } = [];
        public string? Error { get; private set; }

        public void Reset(IBlockProcessingProgress? progress = null, int txIndex = 0, CancellationToken token = default)
        {
            _progress = progress;
            _txIndex = txIndex;
            _token = token;
            HasResult = false;
            Success = false;
            Recipient = null;
            Gas = default;
            Logs = [];
            Error = null;
        }

        public override void MarkAsSuccess(Address recipient, in GasConsumed gasSpent, byte[] output, LogEntry[] logs, Hash256? stateRoot = null)
        {
            HasResult = true;
            Success = true;
            Recipient = recipient;
            Gas = gasSpent;
            Logs = logs;
        }

        public override void MarkAsFailed(Address recipient, in GasConsumed gasSpent, byte[] output, string? error, Hash256? stateRoot = null)
        {
            HasResult = true;
            Success = false;
            Recipient = recipient;
            Gas = gasSpent;
            Error = error;
        }
    }
}
