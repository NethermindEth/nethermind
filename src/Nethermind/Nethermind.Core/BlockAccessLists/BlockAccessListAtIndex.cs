// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
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

    // Hard cap on the recycle bin so a pathological block (tens of thousands of unique accounts
    // touched) doesn't permanently inflate every pooled slice. Sized comfortably above the typical
    // L1/L2 high-water mark; entries beyond the cap are simply dropped to GC.
    private const int MaxPooledAccountChanges = 4096;

    public uint Index { get; set; }

    // Plain Dictionary on the per-tx hot path. The sorted iteration the BAL needs is provided
    // later by GeneratedBlockAccessList._accountChanges (itself a SortedDictionary) when this
    // slice is merged in; sorting on every AddBalanceChange/AddNonceChange/AddStorageChange
    // was O(log n) per call for a property no one between insert and merge consumes.
    private readonly Dictionary<Address, AccountChangesAtIndex> _accountChanges = new();
    private readonly List<Change> _changes = new(InitialChangeCapacity);

    // Parallel revert log holding the previous CodeChange snapshot. Kept separate so the per-change
    // record stays small (CodeChange embeds a managed byte[] plus a 32-byte hash). Code reverts are
    // rare relative to balance / nonce / storage changes; the indirection costs nothing on the hot
    // path. Invariant: for every entry in _changes with Type == CodeChange && HasPrevious, there is
    // one corresponding entry here, in the same order.
    private readonly List<CodeChange> _previousCodeChanges = new();

    // Recycle bin for AccountChangesAtIndex instances released by Clear(). On the next block / tx
    // reuse, GetOrAddAccountChanges prefers popping from here over a fresh allocation, sparing the
    // inner SortedDictionary / SortedSet / Dictionary wrappers.
    private readonly Stack<AccountChangesAtIndex> _accountChangesPool = new();

    // Concrete ValueCollection type rather than IEnumerable so `foreach` binds the struct
    // enumerator and avoids the boxing IEnumerable<T> would force on every iteration.
    public Dictionary<Address, AccountChangesAtIndex>.ValueCollection AccountChanges => _accountChanges.Values;
    public int AccountCount => _accountChanges.Count;
    public bool HasAccount(Address address) => _accountChanges.ContainsKey(address);
    public AccountChangesAtIndex? GetAccountChanges(Address address)
        => _accountChanges.TryGetValue(address, out AccountChangesAtIndex? value) ? value : null;

    public void Clear()
    {
        // Cap retention so the pool can't snowball if one block touches an exceptional number of
        // accounts; surplus instances are dropped to GC.
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
            PreviousValue = new ChangeValue { Balance = previous?.Value ?? default },
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

        // First call for this account: PreTxCode is null; we adopt `before` and know it differs
        // from `after` (we just early-returned the equal case), so skip the redundant compare.
        // Subsequent calls reuse the captured pre-tx baseline; the comparison decides whether the
        // tx net-changed the code (set to current value) or netted to no-op (clear to null).
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
            PreviousValue = new ChangeValue { Nonce = previous?.Value ?? 0 },
        });

        accountChanges.NonceChange = new NonceChange(Index, newNonce);
    }

    public void AddAccountRead(Address address)
    {
        if (!_accountChanges.ContainsKey(address))
        {
            _accountChanges.Add(address, RentAccountChanges(address));
        }
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
            PreviousValue = new ChangeValue { Storage = oldStorageChange?.Value ?? default },
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
                PreviousValue = new ChangeValue { Storage = kv.Value.Value },
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
                PreviousValue = new ChangeValue { Nonce = nonce.Value },
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
                    accountChanges.CodeChange = change.HasPrevious
                        ? codeReverts[--codeCursor]
                        : null;
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

    /// <summary>
    /// Single revert-journal record. Discriminated by <see cref="Type"/>: only the fields applicable
    /// to that variant carry meaningful data, the rest are default-zeroed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Holds a direct <see cref="AccountChangesAtIndex"/> reference (rather than the address) so
    /// <see cref="Restore"/> skips a dictionary lookup per entry. <see cref="HasPrevious"/> indicates
    /// whether a prior in-tx change must be restored or the field simply cleared.
    /// </para>
    /// <para>
    /// <see cref="Slot"/> is only set when <see cref="Type"/> is <see cref="ChangeType.StorageChange"/>.
    /// <see cref="PreviousIndex"/> and <see cref="PreviousValue"/> only carry data when
    /// <see cref="HasPrevious"/> is true. For <see cref="ChangeType.CodeChange"/>, the previous
    /// byte[] payload is held in the parallel <c>_previousCodeChanges</c> list (popped in order
    /// during <see cref="Restore"/>) to keep this struct compact.
    /// </para>
    /// </remarks>
    private readonly struct Change
    {
        public AccountChangesAtIndex Account { get; init; }
        public ChangeType Type { get; init; }
        public bool HasPrevious { get; init; }
        public uint PreviousIndex { get; init; }
        public UInt256 Slot { get; init; }
        public ChangeValue PreviousValue { get; init; }
    }

    /// <summary>
    /// Value payload of a journal entry. Three views of the same 32 bytes; only the field matching
    /// the enclosing <see cref="Change.Type"/> carries meaningful data.
    /// </summary>
    [StructLayout(LayoutKind.Explicit)]
    private struct ChangeValue
    {
        [FieldOffset(0)] public UInt256 Balance;
        [FieldOffset(0)] public ulong Nonce;
        [FieldOffset(0)] public EvmWord Storage;
    }
}
