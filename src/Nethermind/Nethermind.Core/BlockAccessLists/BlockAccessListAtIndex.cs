// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text;
using Nethermind.Core.Resettables;
using Nethermind.Int256;

namespace Nethermind.Core.BlockAccessLists;

/// <summary>
/// BAL changes accumulated for a single index (one transaction). Used during execution to
/// record every state mutation a tx performs. Many of these are produced as the block runs;
/// they are then merged into a <see cref="GeneratedBlockAccessList"/>.
/// Supports <see cref="IJournal{TSnapshot}"/> for tx-level revert and <see cref="IResettable"/>
/// for pooling via <see cref="Caching.StaticPool{T}"/>.
/// </summary>
public class BlockAccessListAtIndex : IJournal<int>, IResettable
{
    public int Index { get; set; }

    private readonly SortedDictionary<Address, AccountChangesAtIndex> _accountChanges
        = new(GenericComparer.GetOptimized<Address>());
    private readonly Stack<Change> _changes = new();

    public IEnumerable<AccountChangesAtIndex> AccountChanges => _accountChanges.Values;
    public int AccountCount => _accountChanges.Count;
    public bool HasAccount(Address address) => _accountChanges.ContainsKey(address);
    public AccountChangesAtIndex? GetAccountChanges(Address address)
        => _accountChanges.TryGetValue(address, out AccountChangesAtIndex? value) ? value : null;

    public void Clear()
    {
        _accountChanges.Clear();
        _changes.Clear();
    }

    public void Reset()
    {
        Clear();
        Index = 0;
    }

    public void AddBalanceChange(Address address, UInt256 before, UInt256 after)
    {
        bool isZeroBalanceChange = before == after;
        if (address == Address.SystemUser && isZeroBalanceChange)
        {
            return;
        }

        AccountChangesAtIndex accountChanges = GetOrAddAccountChanges(address);

        if (isZeroBalanceChange)
        {
            return;
        }

        BalanceChange? oldBalanceChange = accountChanges.BalanceChange;
        accountChanges.BalanceChange = null;

        _changes.Push(new()
        {
            Address = address,
            Type = ChangeType.BalanceChange,
            PreviousBalance = oldBalanceChange,
            PreTxBalance = before,
        });

        bool changedDuringTx = HasBalanceChangedDuringTx(accountChanges, before, after);
        if (changedDuringTx)
        {
            accountChanges.BalanceChange = new BalanceChange(Index, after);
        }
    }

    public void AddCodeChange(Address address, byte[] before, ReadOnlyMemory<byte> after)
    {
        AccountChangesAtIndex accountChanges = GetOrAddAccountChanges(address);

        if (before.AsSpan().SequenceEqual(after.Span))
        {
            return;
        }

        CodeChange? oldCodeChange = accountChanges.CodeChange;
        accountChanges.CodeChange = null;

        _changes.Push(new()
        {
            Address = address,
            Type = ChangeType.CodeChange,
            PreviousCode = oldCodeChange,
            PreTxCode = before,
        });

        bool changedDuringTx = HasCodeChangedDuringTx(accountChanges, before, after.Span);
        if (changedDuringTx)
        {
            accountChanges.CodeChange = new CodeChange(Index, after.ToArray());
        }
    }

    public void AddNonceChange(Address address, ulong newNonce)
    {
        if (newNonce == 0)
        {
            return;
        }

        AccountChangesAtIndex accountChanges = GetOrAddAccountChanges(address);

        NonceChange? oldNonceChange = accountChanges.NonceChange;
        accountChanges.NonceChange = null;

        _changes.Push(new()
        {
            Address = address,
            Type = ChangeType.NonceChange,
            PreviousNonce = oldNonceChange,
        });

        accountChanges.NonceChange = new NonceChange(Index, newNonce);
    }

    public void AddAccountRead(Address address)
    {
        if (!_accountChanges.ContainsKey(address))
        {
            _accountChanges.Add(address, new AccountChangesAtIndex(address));
        }
    }

    public void AddStorageChange(Address address, UInt256 key, UInt256 before, UInt256 after)
    {
        if (before == after) return;

        AccountChangesAtIndex accountChanges = GetOrAddAccountChanges(address);

        accountChanges.TryGetStorageChange(key, out StorageChange? oldStorageChange);
        accountChanges.RemoveStorageChange(key);

        _changes.Push(new()
        {
            Address = address,
            Slot = key,
            Type = ChangeType.StorageChange,
            PreviousStorage = oldStorageChange,
            PreTxStorage = before,
        });

        bool changedDuringTx = HasStorageChangedDuringTx(accountChanges, key, before, after);
        if (changedDuringTx)
        {
            accountChanges.SetStorageChange(key, new StorageChange(Index, after));
            accountChanges.RemoveStorageRead(key);
        }
        else
        {
            accountChanges.AddStorageRead(key);
        }
    }

    public void AddStorageChange(in StorageCell storageCell, UInt256 before, UInt256 after)
        => AddStorageChange(storageCell.Address, storageCell.Index, before, after);

    public void AddStorageRead(in StorageCell storageCell) => AddStorageRead(storageCell.Address, storageCell.Index);

    public void AddStorageRead(Address address, UInt256 key)
    {
        AccountChangesAtIndex accountChanges = GetOrAddAccountChanges(address);
        if (!accountChanges.HasStorageChange(key))
        {
            accountChanges.AddStorageRead(key);
        }
    }

    public void DeleteAccount(Address address, UInt256 oldBalance)
    {
        AccountChangesAtIndex accountChanges = GetOrAddAccountChanges(address);

        // capture current per-slot changes so revert can restore them
        // SortedDictionary doesn't allow modifying while enumerating, so snapshot keys first
        UInt256[] changedSlots = [.. accountChanges.ChangedSlots];
        foreach (UInt256 slot in changedSlots)
        {
            if (accountChanges.TryGetStorageChange(slot, out StorageChange? slotChange))
            {
                _changes.Push(new()
                {
                    Address = address,
                    Type = ChangeType.StorageChange,
                    Slot = slot,
                    PreviousStorage = slotChange,
                });
            }
        }

        if (accountChanges.NonceChange is not null)
        {
            _changes.Push(new()
            {
                Address = address,
                Type = ChangeType.NonceChange,
                PreviousNonce = accountChanges.NonceChange,
            });
        }

        if (accountChanges.CodeChange is not null)
        {
            _changes.Push(new()
            {
                Address = address,
                Type = ChangeType.CodeChange,
                PreviousCode = accountChanges.CodeChange,
            });
        }

        // SELFDESTRUCT clears storage (changes become reads), nonce and code
        foreach (UInt256 slot in changedSlots)
        {
            accountChanges.RemoveStorageChange(slot);
            accountChanges.AddStorageRead(slot);
        }
        accountChanges.NonceChange = null;
        accountChanges.CodeChange = null;

        AddBalanceChange(address, oldBalance, 0);
    }

    public int TakeSnapshot() => _changes.Count;

    public void Restore(int snapshot)
    {
        snapshot = int.Max(0, snapshot);
        while (_changes.Count > snapshot)
        {
            Change change = _changes.Pop();
            AccountChangesAtIndex accountChanges = _accountChanges[change.Address];
            switch (change.Type)
            {
                case ChangeType.BalanceChange:
                    accountChanges.BalanceChange = change.PreviousBalance;
                    break;
                case ChangeType.CodeChange:
                    accountChanges.CodeChange = change.PreviousCode;
                    break;
                case ChangeType.NonceChange:
                    accountChanges.NonceChange = change.PreviousNonce;
                    break;
                case ChangeType.StorageChange:
                    UInt256 slot = change.Slot!.Value;
                    accountChanges.RemoveStorageChange(slot);
                    if (change.PreviousStorage is not null)
                    {
                        accountChanges.SetStorageChange(slot, change.PreviousStorage.Value);
                        accountChanges.RemoveStorageRead(slot);
                    }
                    else
                    {
                        // No prior change in this tx — the slot was accessed (SSTORE implies
                        // SLOAD at the EVM level), so mark it as a read to preserve that the
                        // slot was touched even though the change was reverted.
                        accountChanges.AddStorageRead(slot);
                    }
                    break;
            }
        }
    }

    public override string ToString()
    {
        StringBuilder sb = new();
        sb.AppendLine($"BlockAccessListAtIndex (Index={Index}, Accounts={_accountChanges.Count})");
        foreach (AccountChangesAtIndex ac in _accountChanges.Values)
        {
            sb.Append("  ").Append(ac.Address);
            if (ac.BalanceChange is not null) sb.Append(" balance=").Append(ac.BalanceChange);
            if (ac.NonceChange is not null) sb.Append(" nonce=").Append(ac.NonceChange);
            if (ac.CodeChange is not null) sb.Append(" code=").Append(ac.CodeChange);
            if (ac.StorageChangeCount > 0) sb.Append(" storage=").Append(ac.StorageChangeCount);
            if (ac.StorageReads.Count > 0) sb.Append(" reads=").Append(ac.StorageReads.Count);
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private AccountChangesAtIndex GetOrAddAccountChanges(Address address)
    {
        if (!_accountChanges.TryGetValue(address, out AccountChangesAtIndex? existing))
        {
            existing = new AccountChangesAtIndex(address);
            _accountChanges.Add(address, existing);
        }
        return existing;
    }

    // To detect whether the final value at end-of-tx differs from the pre-tx value, we need
    // the pre-tx value — but the most-recent intra-tx value is stored in the per-account
    // single-slot fields and gets cleared during AddXxx. We recover the pre-tx value by
    // walking _changes for the FIRST push for this (address[, slot]) — that push has
    // PreviousXxx == null, and its PreTxXxx field captured the value before any tx-level
    // modification.
    private bool HasBalanceChangedDuringTx(AccountChangesAtIndex accountChanges, UInt256 beforeInstr, UInt256 afterInstr)
    {
        foreach (Change change in _changes)
        {
            if (change.Type == ChangeType.BalanceChange &&
                change.Address == accountChanges.Address &&
                change.PreviousBalance is null)
            {
                return change.PreTxBalance!.Value != afterInstr;
            }
        }

        // No prior push found — caller must push before invoking, so this is unreachable in
        // normal flow. Fall back to the per-instruction comparison defensively.
        return beforeInstr != afterInstr;
    }

    private bool HasStorageChangedDuringTx(AccountChangesAtIndex accountChanges, UInt256 key, in UInt256 beforeInstr, in UInt256 afterInstr)
    {
        foreach (Change change in _changes)
        {
            if (change.Type == ChangeType.StorageChange &&
                change.Address == accountChanges.Address &&
                change.Slot == key &&
                change.PreviousStorage is null)
            {
                return (change.PreTxStorage ?? default) != afterInstr;
            }
        }

        return beforeInstr != afterInstr;
    }

    private bool HasCodeChangedDuringTx(AccountChangesAtIndex accountChanges, in ReadOnlySpan<byte> beforeInstr, in ReadOnlySpan<byte> afterInstr)
    {
        foreach (Change change in _changes)
        {
            if (change.Type == ChangeType.CodeChange &&
                change.Address == accountChanges.Address &&
                change.PreviousCode is null)
            {
                ReadOnlySpan<byte> preTx = change.PreTxCode ?? [];
                return !preTx.SequenceEqual(afterInstr);
            }
        }

        return !beforeInstr.SequenceEqual(afterInstr);
    }

    private enum ChangeType
    {
        BalanceChange = 0,
        CodeChange = 1,
        NonceChange = 2,
        StorageChange = 3,
    }

    private readonly struct Change
    {
        public Address Address { get; init; }
        public UInt256? Slot { get; init; }
        public ChangeType Type { get; init; }
        public BalanceChange? PreviousBalance { get; init; }
        public NonceChange? PreviousNonce { get; init; }
        public CodeChange? PreviousCode { get; init; }
        public StorageChange? PreviousStorage { get; init; }
        public UInt256? PreTxBalance { get; init; }
        public UInt256? PreTxStorage { get; init; }
        public byte[]? PreTxCode { get; init; }
    }
}
