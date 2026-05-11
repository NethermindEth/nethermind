// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
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
    private const int InitialChangeCapacity = 64;

    public uint Index { get; set; }

    // Plain Dictionary on the per-tx hot path. The sorted iteration the BAL needs is provided
    // later by GeneratedBlockAccessList._accountChanges (itself a SortedDictionary) when this
    // slice is merged in; sorting on every AddBalanceChange/AddNonceChange/AddStorageChange
    // was O(log n) per call for a property no one between insert and merge consumes.
    private readonly Dictionary<Address, AccountChangesAtIndex> _accountChanges = new();
    private readonly List<Change> _changes = new(InitialChangeCapacity);

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

        UInt256 preTxBalance = accountChanges.PreTxBalance ??= before;

        _changes.Add(new()
        {
            Address = address,
            Type = ChangeType.BalanceChange,
            PreviousBalance = accountChanges.BalanceChange,
        });

        accountChanges.BalanceChange = preTxBalance != after
            ? new BalanceChange(Index, after)
            : null;
    }

    public void AddCodeChange(Address address, byte[] before, ReadOnlyMemory<byte> after)
    {
        AccountChangesAtIndex accountChanges = GetOrAddAccountChanges(address);

        if (before.AsSpan().SequenceEqual(after.Span))
        {
            return;
        }

        byte[] preTxCode = accountChanges.PreTxCode ??= before;

        _changes.Add(new()
        {
            Address = address,
            Type = ChangeType.CodeChange,
            PreviousCode = accountChanges.CodeChange,
        });

        accountChanges.CodeChange = !preTxCode.AsSpan().SequenceEqual(after.Span)
            ? new CodeChange(Index, after.ToArray())
            : null;
    }

    public void AddNonceChange(Address address, ulong newNonce)
    {
        // BAL convention: a nonce of 0 means "this tx did not modify the account's nonce" —
        // every active account starts with nonce >= 1 once it has signed any tx, and the
        // post-prestate value visible on the wire is the latest non-zero nonce. Skipping
        // newNonce == 0 keeps the generated BAL aligned with the spec: callers reset state
        // (e.g. EIP-7702 delegation rollback) report 0 to signal "no recorded change",
        // not "set to 0".
        if (newNonce == 0)
        {
            return;
        }

        AccountChangesAtIndex accountChanges = GetOrAddAccountChanges(address);

        _changes.Add(new()
        {
            Address = address,
            Type = ChangeType.NonceChange,
            PreviousNonce = accountChanges.NonceChange,
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

        UInt256 preTxStorage = accountChanges.GetOrCapturePreTxStorage(key, before);

        _changes.Add(new()
        {
            Address = address,
            Slot = key,
            Type = ChangeType.StorageChange,
            PreviousStorage = oldStorageChange,
        });

        if (preTxStorage != after)
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
                _changes.Add(new()
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
            _changes.Add(new()
            {
                Address = address,
                Type = ChangeType.NonceChange,
                PreviousNonce = accountChanges.NonceChange,
            });
        }

        if (accountChanges.CodeChange is not null)
        {
            _changes.Add(new()
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
        Span<Change> span = CollectionsMarshal.AsSpan(_changes);
        int end = span.Length;
        if (snapshot >= end) return;

        for (int i = end - 1; i >= snapshot; i--)
        {
            ref readonly Change change = ref span[i];
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
        CollectionsMarshal.SetCount(_changes, snapshot);
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
    }
}
