// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Evm.GasPolicy;

namespace Nethermind.Evm;

internal sealed class StateGasTracker
{
    private readonly List<StateChange> _changes = [];
    private readonly Stack<AccountingEvent> _accountingEvents = new();
    private readonly HashSet<StorageCell> _createdStorageSlots = new(StorageCell.EqualityComparer);
    private readonly Dictionary<AddressAsKey, int> _createdStorageSlotsByAddress = new(AddressAsKey.EqualityComparer);
    private readonly Dictionary<StorageCell, int> _pendingStorageRefunds = new(StorageCell.EqualityComparer);
    private readonly HashSet<AddressAsKey> _createdAccounts = new(AddressAsKey.EqualityComparer);
    private readonly Dictionary<AddressAsKey, int> _createdCodeLengths = new(AddressAsKey.EqualityComparer);

    public int TakeSnapshot() => _changes.Count;

    public void Restore(int snapshot)
    {
        snapshot = int.Max(0, snapshot);

        while (_accountingEvents.Count != 0 && _accountingEvents.Peek().ChangeCount > snapshot)
        {
            Undo(_accountingEvents.Pop());
        }

        if (_changes.Count > snapshot)
        {
            _changes.RemoveRange(snapshot, _changes.Count - snapshot);
        }
    }

    public void Clear()
    {
        _changes.Clear();
        _accountingEvents.Clear();
        _createdStorageSlots.Clear();
        _createdStorageSlotsByAddress.Clear();
        _pendingStorageRefunds.Clear();
        _createdAccounts.Clear();
        _createdCodeLengths.Clear();
    }

    public void RecordStorageChange(
        in StorageCell storageCell,
        bool transactionEntryIsZero,
        bool beforeIsZero,
        bool afterIsZero)
    {
        if (beforeIsZero == afterIsZero) return;

        StateChangeFlags flags = StateChangeFlags.None;
        if (transactionEntryIsZero) flags |= StateChangeFlags.TxEntryIsZero;
        if (beforeIsZero) flags |= StateChangeFlags.BeforeIsZero;
        if (afterIsZero) flags |= StateChangeFlags.AfterIsZero;

        _changes.Add(new StateChange
        {
            Kind = StateChangeKind.Storage,
            StorageCell = storageCell,
            Flags = flags,
        });
    }

    public void RecordAccountCreated(Address address)
        => _changes.Add(new StateChange { Kind = StateChangeKind.AccountCreated, Address = address });

    public void MarkAccountCreatedForRefund(Address address)
        => _createdAccounts.Add(address);

    public void RecordCodeDeposit(Address address, int codeLength)
    {
        if (codeLength <= 0) return;

        _changes.Add(new StateChange
        {
            Kind = StateChangeKind.CodeDeposit,
            Address = address,
            CodeLength = codeLength,
        });
    }

    public bool ApplyFrameStateGas<TGasPolicy>(int snapshot, ref TGasPolicy gas, long stateGasFloor)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
    {
        snapshot = int.Max(0, snapshot);
        if (snapshot >= _changes.Count) return true;

        // Allocate lazily — the common case is a frame whose only state changes are
        // already-accounted ones from committed children, in which case we exit
        // without touching the heap.
        Dictionary<StorageCell, FrameStorageFlags>? storageAgg = null;
        HashSet<AddressAsKey>? accountAgg = null;
        Dictionary<AddressAsKey, int>? codeAgg = null;
        List<int>? accountedIndices = null;

        Span<StateChange> changesSpan = CollectionsMarshal.AsSpan(_changes);
        for (int i = snapshot; i < changesSpan.Length; i++)
        {
            ref StateChange change = ref changesSpan[i];
            bool accounted = change.Accounted;
            if (!accounted)
            {
                (accountedIndices ??= []).Add(i);
            }

            switch (change.Kind)
            {
                case StateChangeKind.Storage:
                    storageAgg ??= new(StorageCell.EqualityComparer);
                    ref FrameStorageFlags slot = ref CollectionsMarshal.GetValueRefOrAddDefault(storageAgg, change.StorageCell, out bool exists);
                    if (!exists)
                    {
                        FrameStorageFlags init = FrameStorageFlags.None;
                        if (change.TxEntryIsZero) init |= FrameStorageFlags.TxEntryIsZero;
                        if (change.BeforeIsZero) init |= FrameStorageFlags.EntryIsZero;
                        if (change.AfterIsZero) init |= FrameStorageFlags.ExitIsZero;
                        slot = init;
                    }
                    else if (change.AfterIsZero)
                    {
                        slot |= FrameStorageFlags.ExitIsZero;
                    }
                    else
                    {
                        slot &= ~FrameStorageFlags.ExitIsZero;
                    }
                    break;
                case StateChangeKind.AccountCreated:
                    if (accounted) continue;
                    (accountAgg ??= new(AddressAsKey.EqualityComparer)).Add(change.Address!);
                    break;
                case StateChangeKind.CodeDeposit:
                    if (accounted) continue;
                    (codeAgg ??= new(AddressAsKey.EqualityComparer))[change.Address!] = change.CodeLength;
                    break;
            }
        }

        if (accountedIndices is null) return true;

        long stateGasCharge = 0;
        long stateGasRefund = 0;
        long stateGasCredit = 0;
        List<AccountingAction> actions = [];

        if (storageAgg is not null)
        {
            foreach ((StorageCell cell, FrameStorageFlags slot) in storageAgg)
            {
                bool entryIsZero = (slot & FrameStorageFlags.EntryIsZero) != 0;
                bool exitIsZero = (slot & FrameStorageFlags.ExitIsZero) != 0;
                bool txEntryIsZero = (slot & FrameStorageFlags.TxEntryIsZero) != 0;
                bool isCreated = _createdStorageSlots.Contains(cell);
                bool hasPendingRefund = HasPendingStorageRefund(cell);
                long storageSetStateCost = TGasPolicy.GetStorageSetStateCost(in gas);
                if (entryIsZero == exitIsZero)
                {
                    if (entryIsZero && txEntryIsZero && hasPendingRefund)
                    {
                        stateGasCredit = checked(stateGasCredit + storageSetStateCost);
                        actions.Add(AccountingAction.ConsumePendingStorageRefund(cell));
                    }

                    continue;
                }

                if (!exitIsZero && txEntryIsZero && !isCreated)
                {
                    if (hasPendingRefund)
                    {
                        stateGasCredit = checked(stateGasCredit + storageSetStateCost);
                        actions.Add(AccountingAction.ConsumePendingStorageRefund(cell));
                    }

                    stateGasCharge = checked(stateGasCharge + storageSetStateCost);
                    actions.Add(AccountingAction.ChargeStorage(cell));
                }
                else if (exitIsZero && !entryIsZero && txEntryIsZero && isCreated)
                {
                    stateGasRefund = checked(stateGasRefund + storageSetStateCost);
                    actions.Add(AccountingAction.RefundStorage(cell));
                }
                else if (exitIsZero && !entryIsZero && txEntryIsZero)
                {
                    actions.Add(AccountingAction.AddPendingStorageRefund(cell));
                }
            }
        }

        if (accountAgg is not null)
        {
            foreach (AddressAsKey addr in accountAgg)
            {
                if (_createdAccounts.Contains(addr)) continue;
                stateGasCharge = checked(stateGasCharge + TGasPolicy.GetNewAccountStateCost(in gas));
                actions.Add(AccountingAction.ChargeAccount(addr));
            }
        }

        if (codeAgg is not null)
        {
            foreach ((AddressAsKey addr, int len) in codeAgg)
            {
                if (_createdCodeLengths.ContainsKey(addr)) continue;
                stateGasCharge = checked(stateGasCharge + TGasPolicy.GetCodeDepositStateCost(in gas, len));
                actions.Add(AccountingAction.ChargeCodeDeposit(addr, len));
            }
        }

        TGasPolicy gasAfterFrameAccounting = gas;
        if (stateGasRefund > 0)
        {
            TGasPolicy.RefundStateGas(ref gasAfterFrameAccounting, stateGasRefund, stateGasFloor);
        }

        if (stateGasCredit > 0)
        {
            TGasPolicy.CreditStateGasRefund(ref gasAfterFrameAccounting, stateGasCredit);
        }

        if (stateGasCharge > 0 && !TGasPolicy.ConsumeStateGas(ref gasAfterFrameAccounting, stateGasCharge))
        {
            return false;
        }

        gas = gasAfterFrameAccounting;

        for (int i = 0; i < accountedIndices.Count; i++)
        {
            changesSpan[accountedIndices[i]].Accounted = true;
        }

        Apply(actions);
        _accountingEvents.Push(new(_changes.Count, actions, accountedIndices));
        return true;
    }

    public long GetSelfDestructStateRefund<TGasPolicy>(in TGasPolicy gas, Address address)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
    {
        AddressAsKey account = address;
        if (!_createdAccounts.Contains(account)) return 0;

        long refund = TGasPolicy.GetNewAccountStateCost(in gas);
        if (_createdCodeLengths.TryGetValue(account, out int codeLength))
        {
            refund = checked(refund + TGasPolicy.GetCodeDepositStateCost(in gas, codeLength));
        }

        if (_createdStorageSlotsByAddress.TryGetValue(account, out int slotCount) && slotCount > 0)
        {
            long storageSetCost = TGasPolicy.GetStorageSetStateCost(in gas);
            refund = checked(refund + slotCount * storageSetCost);
        }

        return refund;
    }

    private void Apply(List<AccountingAction> actions)
    {
        for (int i = 0; i < actions.Count; i++)
        {
            AccountingAction action = actions[i];
            switch (action.Kind)
            {
                case AccountingActionKind.ChargeStorage:
                    _createdStorageSlots.Add(action.StorageCell);
                    AdjustStorageSlotCount(action.StorageCell.Address, +1);
                    break;
                case AccountingActionKind.RefundStorage:
                    _createdStorageSlots.Remove(action.StorageCell);
                    AdjustStorageSlotCount(action.StorageCell.Address, -1);
                    break;
                case AccountingActionKind.AddPendingStorageRefund:
                    AdjustPendingStorageRefundCount(action.StorageCell, +1);
                    break;
                case AccountingActionKind.ConsumePendingStorageRefund:
                    AdjustPendingStorageRefundCount(action.StorageCell, -1);
                    break;
                case AccountingActionKind.ChargeAccount:
                    _createdAccounts.Add(action.Address);
                    break;
                case AccountingActionKind.ChargeCodeDeposit:
                    _createdCodeLengths[action.Address] = action.CodeLength;
                    break;
            }
        }
    }

    private void Undo(AccountingEvent accountingEvent)
    {
        Span<StateChange> changesSpan = CollectionsMarshal.AsSpan(_changes);
        List<int> indices = accountingEvent.AccountedIndices;
        for (int i = indices.Count - 1; i >= 0; i--)
        {
            changesSpan[indices[i]].Accounted = false;
        }

        List<AccountingAction> actions = accountingEvent.Actions;
        for (int i = actions.Count - 1; i >= 0; i--)
        {
            AccountingAction action = actions[i];
            switch (action.Kind)
            {
                case AccountingActionKind.ChargeStorage:
                    _createdStorageSlots.Remove(action.StorageCell);
                    AdjustStorageSlotCount(action.StorageCell.Address, -1);
                    break;
                case AccountingActionKind.RefundStorage:
                    _createdStorageSlots.Add(action.StorageCell);
                    AdjustStorageSlotCount(action.StorageCell.Address, +1);
                    break;
                case AccountingActionKind.AddPendingStorageRefund:
                    AdjustPendingStorageRefundCount(action.StorageCell, -1);
                    break;
                case AccountingActionKind.ConsumePendingStorageRefund:
                    AdjustPendingStorageRefundCount(action.StorageCell, +1);
                    break;
                case AccountingActionKind.ChargeAccount:
                    _createdAccounts.Remove(action.Address);
                    break;
                case AccountingActionKind.ChargeCodeDeposit:
                    _createdCodeLengths.Remove(action.Address);
                    break;
            }
        }
    }

    private void AdjustStorageSlotCount(AddressAsKey address, int delta)
    {
        ref int count = ref CollectionsMarshal.GetValueRefOrAddDefault(_createdStorageSlotsByAddress, address, out _);
        count += delta;
        if (count == 0)
        {
            _createdStorageSlotsByAddress.Remove(address);
        }
    }

    private bool HasPendingStorageRefund(StorageCell storageCell) =>
        _pendingStorageRefunds.TryGetValue(storageCell, out int count) && count > 0;

    private void AdjustPendingStorageRefundCount(StorageCell storageCell, int delta)
    {
        ref int count = ref CollectionsMarshal.GetValueRefOrAddDefault(_pendingStorageRefunds, storageCell, out _);
        count += delta;
        if (count == 0)
        {
            _pendingStorageRefunds.Remove(storageCell);
        }
    }

    private enum StateChangeKind : byte
    {
        Storage,
        AccountCreated,
        CodeDeposit,
    }

    [Flags]
    private enum StateChangeFlags : byte
    {
        None = 0,
        TxEntryIsZero = 1 << 0,
        BeforeIsZero = 1 << 1,
        AfterIsZero = 1 << 2,
        Accounted = 1 << 3,
    }

    private struct StateChange
    {
        public StateChangeKind Kind;
        public StateChangeFlags Flags;
        public StorageCell StorageCell;
        public Address? Address;
        public int CodeLength;

        public readonly bool TxEntryIsZero => (Flags & StateChangeFlags.TxEntryIsZero) != 0;
        public readonly bool BeforeIsZero => (Flags & StateChangeFlags.BeforeIsZero) != 0;
        public readonly bool AfterIsZero => (Flags & StateChangeFlags.AfterIsZero) != 0;
        public bool Accounted
        {
            readonly get => (Flags & StateChangeFlags.Accounted) != 0;
            set => Flags = value ? Flags | StateChangeFlags.Accounted : Flags & ~StateChangeFlags.Accounted;
        }
    }

    [Flags]
    private enum FrameStorageFlags : byte
    {
        None = 0,
        TxEntryIsZero = 1 << 0,
        EntryIsZero = 1 << 1,
        ExitIsZero = 1 << 2,
    }

    private readonly record struct AccountingEvent(
        int ChangeCount,
        List<AccountingAction> Actions,
        List<int> AccountedIndices);

    private readonly record struct AccountingAction(
        AccountingActionKind Kind,
        StorageCell StorageCell,
        AddressAsKey Address,
        int CodeLength)
    {
        public static AccountingAction ChargeStorage(StorageCell storageCell) =>
            new(AccountingActionKind.ChargeStorage, storageCell, default, 0);

        public static AccountingAction RefundStorage(StorageCell storageCell) =>
            new(AccountingActionKind.RefundStorage, storageCell, default, 0);

        public static AccountingAction AddPendingStorageRefund(StorageCell storageCell) =>
            new(AccountingActionKind.AddPendingStorageRefund, storageCell, default, 0);

        public static AccountingAction ConsumePendingStorageRefund(StorageCell storageCell) =>
            new(AccountingActionKind.ConsumePendingStorageRefund, storageCell, default, 0);

        public static AccountingAction ChargeAccount(AddressAsKey address) =>
            new(AccountingActionKind.ChargeAccount, default, address, 0);

        public static AccountingAction ChargeCodeDeposit(AddressAsKey address, int codeLength) =>
            new(AccountingActionKind.ChargeCodeDeposit, default, address, codeLength);
    }

    private enum AccountingActionKind : byte
    {
        ChargeStorage,
        RefundStorage,
        AddPendingStorageRefund,
        ConsumePendingStorageRefund,
        ChargeAccount,
        ChargeCodeDeposit,
    }
}
