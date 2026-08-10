// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Int256;

namespace Nethermind.Evm;

/// <summary>
/// EIP-7906: an ordered, net-collapsed snapshot of a transaction's state diff and logs, built once
/// per frame transaction and shared by its POST_TX frames.
/// </summary>
/// <remarks>
/// The diff data is read straight from the in-flight BAL slice (<see cref="BlockAccessListAtIndex"/>),
/// which already collapses intermediate writes to one entry per (address, slot) and captures the
/// transaction-prestate ("before") values. This type only fixes the enumeration order the opcodes
/// require: balances and storage sorted ascending by address (uint160) then slot key (uint256),
/// events in emission order. Caching is safe because a POST_TX frame is static, so the diff cannot
/// change once assertions start running.
/// </remarks>
internal sealed class Eip7906DiffView
{
    public readonly record struct SlotRef(Address Address, UInt256 Key);

    public BlockAccessListAtIndex Slice { get; }

    /// <summary>Addresses with a net balance change, ascending.</summary>
    public Address[] BalanceAddresses { get; }

    /// <summary>Changed slots across all accounts, ascending by (address, key); an address's slots are contiguous.</summary>
    public SlotRef[] Slots { get; }

    /// <summary>Newly-deployed contracts, ascending.</summary>
    public Address[] DeployedAddresses { get; }

    /// <summary>Logs emitted by the transaction, in emission order.</summary>
    public LogEntry[] Logs { get; }

    private Eip7906DiffView(BlockAccessListAtIndex slice, Address[] balanceAddresses, SlotRef[] slots, Address[] deployedAddresses, LogEntry[] logs)
    {
        Slice = slice;
        BalanceAddresses = balanceAddresses;
        Slots = slots;
        DeployedAddresses = deployedAddresses;
        Logs = logs;
    }

    public static Eip7906DiffView Build(BlockAccessListAtIndex slice, LogEntry[] logs)
    {
        List<AccountChangesAtIndex> accounts = new(slice.AccountCount);
        foreach (AccountChangesAtIndex account in slice.AccountChanges) accounts.Add(account);
        accounts.Sort(static (x, y) => CompareAddress(x.Address, y.Address));

        List<Address> balances = [];
        List<Address> deployed = [];
        List<SlotRef> slots = [];
        foreach (AccountChangesAtIndex account in accounts)
        {
            if (account.BalanceChange is not null) balances.Add(account.Address);
            if (IsDeployment(account)) deployed.Add(account.Address);

            int slotCount = account.StorageChangeCount;
            if (slotCount > 0)
            {
                UInt256[] keys = new UInt256[slotCount];
                int i = 0;
                foreach (UInt256 key in account.ChangedSlots) keys[i++] = key;
                Array.Sort(keys);
                foreach (UInt256 key in keys) slots.Add(new SlotRef(account.Address, key));
            }
        }

        return new Eip7906DiffView(slice, balances.ToArray(), slots.ToArray(), deployed.ToArray(), logs);
    }

    // A contract deployment is code appearing where the account previously had none (CREATE/CREATE2).
    private static bool IsDeployment(AccountChangesAtIndex account)
        => account.CodeChange is not null && (account.PreTxCode is null || account.PreTxCode.Length == 0);

    // Ascending uint160: big-endian byte comparison of the 20-byte address matches numeric order.
    private static int CompareAddress(Address a, Address b)
        => a.Bytes.SequenceCompareTo(b.Bytes);
}
