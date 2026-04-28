// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Evm.GasPolicy;

namespace Nethermind.Evm;

internal sealed class StateGasTracker
{
    private readonly List<StateChange> _changes = [];
    private readonly Stack<AccountingEvent> _accountingEvents = new();
    private readonly HashSet<StorageCell> _createdStorageSlots = new(StorageCell.EqualityComparer);
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
        _createdAccounts.Clear();
        _createdCodeLengths.Clear();
    }

    public void RecordStorageChange(
        in StorageCell storageCell,
        bool transactionEntryIsZero,
        bool beforeIsZero,
        bool afterIsZero)
    {
        if (beforeIsZero == afterIsZero)
        {
            return;
        }

        _changes.Add(new StorageStateChange(
            storageCell,
            transactionEntryIsZero,
            beforeIsZero,
            afterIsZero));
    }

    public void RecordAccountCreated(Address address)
        => _changes.Add(new AccountCreatedStateChange(address));

    public void RecordCodeDeposit(Address address, int codeLength)
    {
        if (codeLength <= 0)
        {
            return;
        }

        _changes.Add(new CodeDepositStateChange(address, codeLength));
    }

    public bool ApplyFrameStateGas<TGasPolicy>(int snapshot, ref TGasPolicy gas, long stateGasFloor)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
    {
        snapshot = int.Max(0, snapshot);
        if (snapshot >= _changes.Count)
        {
            return true;
        }

        Dictionary<StorageCell, FrameStorageChange> storageChanges = new(StorageCell.EqualityComparer);
        Dictionary<AddressAsKey, FrameAccountChange> accountChanges = new(AddressAsKey.EqualityComparer);
        Dictionary<AddressAsKey, FrameCodeDepositChange> codeDepositChanges = new(AddressAsKey.EqualityComparer);

        for (int i = snapshot; i < _changes.Count; i++)
        {
            StateChange change = _changes[i];
            if (change.Accounted)
            {
                continue;
            }

            switch (change)
            {
                case StorageStateChange storageChange:
                    if (!storageChanges.TryGetValue(storageChange.StorageCell, out FrameStorageChange? frameStorageChange))
                    {
                        frameStorageChange = new(storageChange);
                        storageChanges.Add(storageChange.StorageCell, frameStorageChange);
                    }
                    else
                    {
                        frameStorageChange.ExitIsZero = storageChange.AfterIsZero;
                    }
                    frameStorageChange.Changes.Add(storageChange);
                    break;
                case AccountCreatedStateChange accountChange:
                    AddressAsKey accountKey = accountChange.Address;
                    if (!accountChanges.TryGetValue(accountKey, out FrameAccountChange? frameAccountChange))
                    {
                        frameAccountChange = new(accountChange.Address);
                        accountChanges.Add(accountKey, frameAccountChange);
                    }
                    frameAccountChange.Changes.Add(accountChange);
                    break;
                case CodeDepositStateChange codeDepositChange:
                    AddressAsKey codeKey = codeDepositChange.Address;
                    if (!codeDepositChanges.TryGetValue(codeKey, out FrameCodeDepositChange? frameCodeDepositChange))
                    {
                        frameCodeDepositChange = new(codeDepositChange.Address);
                        codeDepositChanges.Add(codeKey, frameCodeDepositChange);
                    }
                    frameCodeDepositChange.CodeLength = codeDepositChange.CodeLength;
                    frameCodeDepositChange.Changes.Add(codeDepositChange);
                    break;
            }
        }

        long stateGasCharge = 0;
        long stateGasRefund = 0;
        List<AccountingAction> actions = [];
        List<StateChange> accountedChanges = [];

        foreach (FrameStorageChange storageChange in storageChanges.Values)
        {
            accountedChanges.AddRange(storageChange.Changes);

            if (storageChange.EntryIsZero == storageChange.ExitIsZero)
            {
                continue;
            }

            if (!storageChange.ExitIsZero && storageChange.TransactionEntryIsZero && !_createdStorageSlots.Contains(storageChange.StorageCell))
            {
                stateGasCharge = checked(stateGasCharge + TGasPolicy.GetStorageSetStateCost(in gas));
                actions.Add(AccountingAction.ChargeStorage(storageChange.StorageCell));
            }
            else if (storageChange.ExitIsZero &&
                     !storageChange.EntryIsZero &&
                     storageChange.TransactionEntryIsZero &&
                     _createdStorageSlots.Contains(storageChange.StorageCell))
            {
                stateGasRefund = checked(stateGasRefund + TGasPolicy.GetStorageSetStateCost(in gas));
                actions.Add(AccountingAction.RefundStorage(storageChange.StorageCell));
            }
        }

        foreach (FrameAccountChange accountChange in accountChanges.Values)
        {
            accountedChanges.AddRange(accountChange.Changes);

            if (_createdAccounts.Contains(accountChange.Address))
            {
                continue;
            }

            stateGasCharge = checked(stateGasCharge + TGasPolicy.GetNewAccountStateCost(in gas));
            actions.Add(AccountingAction.ChargeAccount(accountChange.Address));
        }

        foreach (FrameCodeDepositChange codeDepositChange in codeDepositChanges.Values)
        {
            accountedChanges.AddRange(codeDepositChange.Changes);

            if (_createdCodeLengths.ContainsKey(codeDepositChange.Address))
            {
                continue;
            }

            stateGasCharge = checked(stateGasCharge + TGasPolicy.GetCodeDepositStateCost(in gas, codeDepositChange.CodeLength));
            actions.Add(AccountingAction.ChargeCodeDeposit(codeDepositChange.Address, codeDepositChange.CodeLength));
        }

        if (accountedChanges.Count == 0)
        {
            return true;
        }

        TGasPolicy gasAfterFrameAccounting = gas;
        if (stateGasRefund > 0)
        {
            TGasPolicy.RefundStateGas(ref gasAfterFrameAccounting, stateGasRefund, stateGasFloor);
        }

        if (stateGasCharge > 0 && !TGasPolicy.ConsumeStateGas(ref gasAfterFrameAccounting, stateGasCharge))
        {
            return false;
        }

        gas = gasAfterFrameAccounting;

        for (int i = 0; i < accountedChanges.Count; i++)
        {
            accountedChanges[i].Accounted = true;
        }

        Apply(actions);
        _accountingEvents.Push(new(_changes.Count, actions, accountedChanges));
        return true;
    }

    public long GetSelfDestructStateRefund<TGasPolicy>(in TGasPolicy gas, Address address)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
    {
        AddressAsKey account = address;
        if (!_createdAccounts.Contains(account))
        {
            return 0;
        }

        long refund = TGasPolicy.GetNewAccountStateCost(in gas);
        if (_createdCodeLengths.TryGetValue(account, out int codeLength))
        {
            refund = checked(refund + TGasPolicy.GetCodeDepositStateCost(in gas, codeLength));
        }

        foreach (StorageCell storageCell in _createdStorageSlots)
        {
            if (storageCell.Address == address)
            {
                refund = checked(refund + TGasPolicy.GetStorageSetStateCost(in gas));
            }
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
                    break;
                case AccountingActionKind.RefundStorage:
                    _createdStorageSlots.Remove(action.StorageCell);
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
        for (int i = accountingEvent.AccountedChanges.Count - 1; i >= 0; i--)
        {
            accountingEvent.AccountedChanges[i].Accounted = false;
        }

        for (int i = accountingEvent.Actions.Count - 1; i >= 0; i--)
        {
            AccountingAction action = accountingEvent.Actions[i];
            switch (action.Kind)
            {
                case AccountingActionKind.ChargeStorage:
                    _createdStorageSlots.Remove(action.StorageCell);
                    break;
                case AccountingActionKind.RefundStorage:
                    _createdStorageSlots.Add(action.StorageCell);
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

    private abstract class StateChange
    {
        public bool Accounted { get; set; }
    }

    private sealed class StorageStateChange(
        StorageCell storageCell,
        bool transactionEntryIsZero,
        bool beforeIsZero,
        bool afterIsZero) : StateChange
    {
        public StorageCell StorageCell { get; } = storageCell;
        public bool TransactionEntryIsZero { get; } = transactionEntryIsZero;
        public bool BeforeIsZero { get; } = beforeIsZero;
        public bool AfterIsZero { get; } = afterIsZero;
    }

    private sealed class AccountCreatedStateChange(Address address) : StateChange
    {
        public Address Address { get; } = address;
    }

    private sealed class CodeDepositStateChange(Address address, int codeLength) : StateChange
    {
        public Address Address { get; } = address;
        public int CodeLength { get; } = codeLength;
    }

    private sealed class FrameStorageChange(StorageStateChange firstChange)
    {
        public StorageCell StorageCell { get; } = firstChange.StorageCell;
        public bool TransactionEntryIsZero { get; } = firstChange.TransactionEntryIsZero;
        public bool EntryIsZero { get; } = firstChange.BeforeIsZero;
        public bool ExitIsZero { get; set; } = firstChange.AfterIsZero;
        public List<StateChange> Changes { get; } = [firstChange];
    }

    private sealed class FrameAccountChange(Address address)
    {
        public Address Address { get; } = address;
        public List<StateChange> Changes { get; } = [];
    }

    private sealed class FrameCodeDepositChange(Address address)
    {
        public Address Address { get; } = address;
        public int CodeLength { get; set; }
        public List<StateChange> Changes { get; } = [];
    }

    private readonly record struct AccountingEvent(
        int ChangeCount,
        List<AccountingAction> Actions,
        List<StateChange> AccountedChanges);

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

        public static AccountingAction ChargeAccount(Address address) =>
            new(AccountingActionKind.ChargeAccount, default, address, 0);

        public static AccountingAction ChargeCodeDeposit(Address address, int codeLength) =>
            new(AccountingActionKind.ChargeCodeDeposit, default, address, codeLength);
    }

    private enum AccountingActionKind
    {
        ChargeStorage,
        RefundStorage,
        ChargeAccount,
        ChargeCodeDeposit
    }
}
