// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Nethermind.Core.Collections;
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

    // Caps pool retention so a one-off oversized block doesn't permanently inflate the slice.
    private const int MaxPooledAccountChanges = 4096;

    public uint Index { get; set; }

    private readonly Dictionary<AddressAsKey, AccountChangesAtIndex> _accountChanges = new(GenericEqualityComparer.GetOptimized<AddressAsKey>());
    private readonly List<Change> _changes = new(InitialChangeCapacity);

    private readonly List<CodeChange> _previousCodeChanges = [];

    private readonly Stack<AccountChangesAtIndex> _accountChangesPool = new();

    // Single-slot cache: repeated same-address reads skip the dict probe. Reset in Clear() (pooled);
    // safe as entries are never individually removed.
    private Address? _lastReadAddress;
    private AccountChangesAtIndex? _lastReadChanges;

    public Dictionary<AddressAsKey, AccountChangesAtIndex>.ValueCollection AccountChanges => _accountChanges.Values;
    public int AccountCount => _accountChanges.Count;
    public bool HasAccount(Address address) => _accountChanges.ContainsKey(address);
    public AccountChangesAtIndex? GetAccountChanges(Address address)
    {
        // Read-only fast path reusing the single-slot cache (see RecordReadAndGet). Never creates an
        // entry; only memoizes a dict hit, so it cannot change recorded BAL contents.
        if (address.Equals(_lastReadAddress)) return _lastReadChanges;
        if (_accountChanges.TryGetValue(address, out AccountChangesAtIndex? value))
        {
            _lastReadAddress = address;
            _lastReadChanges = value;
            return value;
        }
        return null;
    }

    public void Clear()
    {
        int spareCount = _accountChangesPool.Count;
        foreach (AccountChangesAtIndex value in _accountChanges.Values)
        {
            if (spareCount >= MaxPooledAccountChanges) break;
            _accountChangesPool.Push(value);
            spareCount++;
        }
        _accountChanges.Clear();
        _changes.Clear();
        _previousCodeChanges.Clear();
        _lastReadAddress = null;
        _lastReadChanges = null;
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

        // Don't add zero balance transfers, but DO add empty account changes — EELS/pyspec
        // include touched-but-unchanged accounts (e.g. EIP-1559 zero-tip coinbase credits) in
        // the suggested BAL, so dropping them on our side would diverge from the BAL hash.
        if (isZeroBalanceChange)
        {
            return;
        }

        UInt256 preTxBalance = accountChanges.PreTxBalance ??= before;

        BalanceChange? previous = accountChanges.BalanceChange;
        _changes.Add(new Change
        {
            Account = accountChanges,
            Type = ChangeType.BalanceChange,
            HasPrevious = previous.HasValue,
            PreviousIndex = previous?.Index ?? 0,
            PreviousValue = new ChangeValue(previous?.Value ?? default),
        });

        accountChanges.BalanceChange = preTxBalance != after
            ? new BalanceChange(Index, after)
            : null;
    }

    public void AddCodeChange(Address address, byte[] before, ReadOnlyMemory<byte> after)
    {
        if (before.AsSpan().SequenceEqual(after.Span))
        {
            return;
        }

        AccountChangesAtIndex accountChanges = GetOrAddAccountChanges(address);

        bool isFirstCall = accountChanges.PreTxCode is null;
        byte[] preTxCode = accountChanges.PreTxCode ??= before;

        CodeChange? previous = accountChanges.CodeChange;
        if (previous.HasValue) _previousCodeChanges.Add(previous.Value);
        _changes.Add(new Change
        {
            Account = accountChanges,
            Type = ChangeType.CodeChange,
            HasPrevious = previous.HasValue,
        });

        accountChanges.CodeChange = isFirstCall || !preTxCode.AsSpan().SequenceEqual(after.Span)
            ? new CodeChange(Index, after.ToArray())
            : null;
    }

    public void AddNonceChange(Address address, ulong newNonce)
    {
        if (newNonce == 0)
        {
            return;
        }

        AccountChangesAtIndex accountChanges = GetOrAddAccountChanges(address);

        NonceChange? previous = accountChanges.NonceChange;
        _changes.Add(new Change
        {
            Account = accountChanges,
            Type = ChangeType.NonceChange,
            HasPrevious = previous.HasValue,
            PreviousIndex = previous?.Index ?? 0,
            PreviousValue = new ChangeValue(previous?.Value ?? 0UL),
        });

        accountChanges.NonceChange = new NonceChange(Index, newNonce);
    }

    public void AddAccountRead(Address address) => RecordReadAndGet(address);

    /// <summary>Records an account read and returns its entry; one-slot cache skips repeat same-address probes.</summary>
    public AccountChangesAtIndex RecordReadAndGet(Address address)
    {
        if (!address.Equals(_lastReadAddress))
        {
            _lastReadChanges = GetOrAddAccountChanges(address);
            _lastReadAddress = address;
        }
        return _lastReadChanges!;
    }

    /// <summary>Records a storage-slot read and returns the account entry in a single account resolution.</summary>
    public AccountChangesAtIndex RecordStorageReadAndGet(Address address, UInt256 key)
    {
        AccountChangesAtIndex accountChanges = RecordReadAndGet(address);
        if (!accountChanges.HasStorageChange(key))
            accountChanges.AddStorageRead(key);
        return accountChanges;
    }

    public void AddStorageChange(Address address, UInt256 key, UInt256 before, UInt256 after)
    {
        if (before == after) return;

        AccountChangesAtIndex accountChanges = GetOrAddAccountChanges(address);

        accountChanges.TryRemoveStorageChange(key, out StorageChange? oldStorageChange);

        UInt256 preTxStorage = accountChanges.GetOrCapturePreTxStorage(key, before);

        _changes.Add(new Change
        {
            Account = accountChanges,
            Slot = key,
            Type = ChangeType.StorageChange,
            HasPrevious = oldStorageChange.HasValue,
            PreviousIndex = oldStorageChange?.Index ?? 0,
            PreviousValue = new ChangeValue(oldStorageChange?.Value ?? default),
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

        using ArrayPoolListRef<UInt256> changedSlots = new(accountChanges.StorageChangeCount);
        foreach (KeyValuePair<UInt256, StorageChange> kv in accountChanges.StorageChanges)
        {
            changedSlots.Add(kv.Key);
            _changes.Add(new Change
            {
                Account = accountChanges,
                Type = ChangeType.StorageChange,
                Slot = kv.Key,
                HasPrevious = true,
                PreviousIndex = kv.Value.Index,
                PreviousValue = new ChangeValue(kv.Value.Value),
            });
        }

        if (accountChanges.NonceChange is { } nonce)
        {
            _changes.Add(new Change
            {
                Account = accountChanges,
                Type = ChangeType.NonceChange,
                HasPrevious = true,
                PreviousIndex = nonce.Index,
                PreviousValue = new ChangeValue(nonce.Value),
            });
        }

        if (accountChanges.CodeChange is { } code)
        {
            _previousCodeChanges.Add(code);
            _changes.Add(new Change
            {
                Account = accountChanges,
                Type = ChangeType.CodeChange,
                HasPrevious = true,
            });
        }

        // SELFDESTRUCT clears storage (changes become reads), nonce and code
        foreach (UInt256 slot in changedSlots.AsSpan())
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
        // Intentionally does not reset _lastReadAddress/_lastReadChanges: Restore reverts entry values
        // in place and never evicts an entry, so the cached reference stays valid. See class invariant.
        snapshot = int.Max(0, snapshot);
        Span<Change> span = CollectionsMarshal.AsSpan(_changes);
        int end = span.Length;
        if (snapshot >= end) return;

        Span<CodeChange> codeReverts = CollectionsMarshal.AsSpan(_previousCodeChanges);
        int codeCursor = codeReverts.Length;

        for (int i = end - 1; i >= snapshot; i--)
        {
            ref readonly Change change = ref span[i];
            AccountChangesAtIndex accountChanges = change.Account;
            switch (change.Type)
            {
                case ChangeType.BalanceChange:
                    accountChanges.BalanceChange = change.HasPrevious
                        ? new BalanceChange(change.PreviousIndex, change.PreviousValue.Balance)
                        : null;
                    break;
                case ChangeType.CodeChange:
                    if (change.HasPrevious)
                    {
                        Debug.Assert(codeCursor > 0, "Code revert cursor underflow: _previousCodeChanges count does not match _changes journal.");
                        accountChanges.CodeChange = codeReverts[--codeCursor];
                    }
                    else
                    {
                        accountChanges.CodeChange = null;
                    }
                    break;
                case ChangeType.NonceChange:
                    accountChanges.NonceChange = change.HasPrevious
                        ? new NonceChange(change.PreviousIndex, change.PreviousValue.Nonce)
                        : null;
                    break;
                case ChangeType.StorageChange:
                    UInt256 slot = change.Slot;
                    accountChanges.RemoveStorageChange(slot);
                    if (change.HasPrevious)
                    {
                        accountChanges.SetStorageChange(slot, new StorageChange(change.PreviousIndex, change.PreviousValue.Storage));
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
        CollectionsMarshal.SetCount(_previousCodeChanges, codeCursor);
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
            existing = RentAccountChanges(address);
            _accountChanges.Add(address, existing);
        }
        return existing;
    }

    private AccountChangesAtIndex RentAccountChanges(Address address)
    {
        if (_accountChangesPool.TryPop(out AccountChangesAtIndex? recycled))
        {
            recycled.Reset(address);
            return recycled;
        }
        return new AccountChangesAtIndex(address);
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
        public AccountChangesAtIndex Account { get; init; }
        public ChangeType Type { get; init; }
        public bool HasPrevious { get; init; }
        public uint PreviousIndex { get; init; }
        public UInt256 Slot { get; init; }
        public ChangeValue PreviousValue { get; init; }
    }

    private readonly struct ChangeValue
    {
        private readonly UInt256 _data;

        public ChangeValue(UInt256 balance) => _data = balance;

        public ChangeValue(ulong nonce) => _data = new UInt256(nonce);

        public ChangeValue(in EvmWord storage) => _data = Unsafe.As<EvmWord, UInt256>(ref Unsafe.AsRef(in storage));

        public UInt256 Balance => _data;

        public ulong Nonce => _data.u0;

        public EvmWord Storage => Unsafe.As<UInt256, EvmWord>(ref Unsafe.AsRef(in _data));
    }
}
