// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Evm;

/// <summary>EIP-7906: ordered view of a transaction's state diff and logs, shared by its ASSERT frames.</summary>
/// <remarks>The BAL slice already net-collapses writes and holds the prestate; this adds only the spec's
/// enumeration order.</remarks>
internal sealed class TransactionDiffView
{
    public readonly record struct SlotRef(Address Address, UInt256 Key);

    public BlockAccessListAtIndex Slice { get; }

    /// <summary>Addresses with a net balance change, ascending.</summary>
    public Address[] BalanceAddresses { get; }

    /// <summary>Changed slots across all accounts, ascending by (address, key); an address's slots are contiguous.</summary>
    public SlotRef[] Slots { get; }

    /// <summary>Addresses with a net code hash change, ascending.</summary>
    public Address[] CodeAddresses { get; }

    /// <summary>Addresses with a net nonce change, ascending.</summary>
    public Address[] NonceAddresses { get; }

    /// <summary>Logs emitted by the transaction, in emission order.</summary>
    public LogEntry[] Logs { get; }

    private Dictionary<AddressAsKey, ValueHash256>? _preTxCodeHashes;

    private TransactionDiffView(
        BlockAccessListAtIndex slice,
        Address[] balanceAddresses,
        SlotRef[] slots,
        Address[] codeAddresses,
        Address[] nonceAddresses,
        LogEntry[] logs)
    {
        Slice = slice;
        BalanceAddresses = balanceAddresses;
        Slots = slots;
        CodeAddresses = codeAddresses;
        NonceAddresses = nonceAddresses;
        Logs = logs;
    }

    public static TransactionDiffView Build(BlockAccessListAtIndex slice, LogEntry[] logs)
    {
        // AccountChanges also holds read-only accesses; filtering first keeps the sort down to the diff.
        List<AccountChangesAtIndex> accounts = new(slice.AccountCount);
        int balanceCount = 0, codeCount = 0, nonceCount = 0, slotCount = 0;
        foreach (AccountChangesAtIndex account in slice.AccountChanges)
        {
            bool hasBalance = account.BalanceChange is not null;
            bool hasCode = account.CodeChange is not null;
            bool hasNonce = HasNonceChange(account);
            int accountSlots = account.StorageChangeCount;
            if (!hasBalance && accountSlots == 0 && !hasCode && !hasNonce) continue;

            accounts.Add(account);
            if (hasBalance) balanceCount++;
            if (hasCode) codeCount++;
            if (hasNonce) nonceCount++;
            // Exact sizing holds because StorageChangeCount counts the same dictionary the fill pass enumerates.
            slotCount += accountSlots;
        }
        CollectionsMarshal.AsSpan(accounts).Sort(default(AddressOrder));

        Address[] balances = balanceCount == 0 ? [] : new Address[balanceCount];
        Address[] codes = codeCount == 0 ? [] : new Address[codeCount];
        Address[] nonces = nonceCount == 0 ? [] : new Address[nonceCount];
        SlotRef[] slots = slotCount == 0 ? [] : new SlotRef[slotCount];
        int balanceAt = 0, codeAt = 0, nonceAt = 0, slotAt = 0;
        foreach (AccountChangesAtIndex account in accounts)
        {
            if (account.BalanceChange is not null) balances[balanceAt++] = account.Address;
            if (account.CodeChange is not null) codes[codeAt++] = account.Address;
            if (HasNonceChange(account)) nonces[nonceAt++] = account.Address;

            int accountSlots = account.StorageChangeCount;
            if (accountSlots > 0)
            {
                int start = slotAt;
                foreach (UInt256 key in account.StorageChanges.Keys) slots[slotAt++] = new SlotRef(account.Address, key);
                // One address per run, so ordering the run by key alone yields the spec's (address, key) order.
                slots.AsSpan(start, accountSlots).Sort(default(SlotKeyOrder));
            }
        }

        return new TransactionDiffView(slice, balances, slots, codes, nonces, logs);
    }

    /// <summary>Pre-tx code hash for an address whose code changed, memoized for the life of this view.</summary>
    public ValueHash256 GetPreTxCodeHash(Address address, AccountChangesAtIndex account)
    {
        _preTxCodeHashes ??= [];
        ref ValueHash256 hash = ref CollectionsMarshal.GetValueRefOrAddDefault(_preTxCodeHashes, address, out bool exists);
        if (!exists) hash = ValueKeccak.Compute(account.PreTxCode.Span);
        return hash;
    }

    /// <summary>Spec <c>nonces_changed</c>: the nonce differs from its transaction prestate, a non-existent account counting as 0.</summary>
    private static bool HasNonceChange(AccountChangesAtIndex account)
        => account.NonceChange is { } nonce && nonce.Value != (account.PreTxNonce ?? 0);

    // Ascending uint160: big-endian byte comparison of the 20-byte address matches numeric order.
    private readonly struct AddressOrder : IComparer<AccountChangesAtIndex>
    {
        public int Compare(AccountChangesAtIndex? x, AccountChangesAtIndex? y) => x!.Address.Bytes.SequenceCompareTo(y!.Address.Bytes);
    }

    private readonly struct SlotKeyOrder : IComparer<SlotRef>
    {
        public int Compare(SlotRef x, SlotRef y) => x.Key.CompareTo(y.Key);
    }
}
