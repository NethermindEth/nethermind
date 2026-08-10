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

/// <summary>
/// EIP-7906: an ordered, net-collapsed snapshot of a transaction's state diff and logs, built once
/// per frame transaction and shared by its POST_TX frames.
/// </summary>
/// <remarks>
/// The diff data is read straight from the in-flight BAL slice (<see cref="BlockAccessListAtIndex"/>),
/// which already collapses intermediate writes to one entry per (address, slot) and captures the
/// transaction-prestate ("before") values. This type fixes the enumeration order the opcodes require
/// (balances and storage sorted ascending by address then slot key; events in emission order) and
/// precomputes per-address indexes so the keyed TXDIFF views resolve in O(1) rather than scanning the
/// whole diff per call. Caching is safe because a POST_TX frame is static, so the diff cannot change
/// once assertions start running.
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

    // Contiguous [start, start+count) run within Slots for each address (TXDIFF 0x06/0x07).
    private readonly Dictionary<AddressAsKey, (int Start, int Count)> _slotRuns;
    // Global Logs indices, in emission order, per emitting address (TXDIFF 0x08/0x09).
    private readonly Dictionary<AddressAsKey, int[]> _eventIndices;
    // Memoized pre-tx code hashes, so repeated TXDIFF(0x04) calls don't re-hash the pre-tx code.
    private Dictionary<AddressAsKey, ValueHash256>? _preTxCodeHashes;

    private Eip7906DiffView(
        BlockAccessListAtIndex slice,
        Address[] balanceAddresses,
        SlotRef[] slots,
        Address[] deployedAddresses,
        LogEntry[] logs,
        Dictionary<AddressAsKey, (int, int)> slotRuns,
        Dictionary<AddressAsKey, int[]> eventIndices)
    {
        Slice = slice;
        BalanceAddresses = balanceAddresses;
        Slots = slots;
        DeployedAddresses = deployedAddresses;
        Logs = logs;
        _slotRuns = slotRuns;
        _eventIndices = eventIndices;
    }

    public static Eip7906DiffView Build(BlockAccessListAtIndex slice, LogEntry[] logs)
    {
        // slice.AccountChanges also holds read-only accesses (AddAccountRead creates entries); keep only
        // the accounts that actually changed so the sort is proportional to the diff, not the access set.
        List<AccountChangesAtIndex> accounts = [];
        foreach (AccountChangesAtIndex account in slice.AccountChanges)
        {
            if (account.BalanceChange is not null || account.StorageChangeCount > 0 || IsDeployment(account))
                accounts.Add(account);
        }
        accounts.Sort(static (x, y) => CompareAddress(x.Address, y.Address));

        List<Address> balances = [];
        List<Address> deployed = [];
        List<SlotRef> slots = [];
        Dictionary<AddressAsKey, (int, int)> slotRuns = [];
        foreach (AccountChangesAtIndex account in accounts)
        {
            if (account.BalanceChange is not null) balances.Add(account.Address);
            if (IsDeployment(account)) deployed.Add(account.Address);

            int slotCount = account.StorageChangeCount;
            if (slotCount > 0)
            {
                int start = slots.Count;
                UInt256[] keys = new UInt256[slotCount];
                int i = 0;
                foreach (UInt256 key in account.ChangedSlots) keys[i++] = key;
                Array.Sort(keys);
                foreach (UInt256 key in keys) slots.Add(new SlotRef(account.Address, key));
                slotRuns[account.Address] = (start, slotCount);
            }
        }

        Dictionary<AddressAsKey, int[]> eventIndices = GroupEventIndicesByAddress(logs);
        return new Eip7906DiffView(slice, [.. balances], [.. slots], [.. deployed], logs, slotRuns, eventIndices);
    }

    public bool TryGetSlotRun(Address address, out int start, out int count)
    {
        if (_slotRuns.TryGetValue(address, out (int Start, int Count) run))
        {
            (start, count) = run;
            return true;
        }
        start = count = 0;
        return false;
    }

    public int AddressEventCount(Address address)
        => _eventIndices.TryGetValue(address, out int[]? indices) ? indices.Length : 0;

    public bool TryGetAddressEventGlobalIndex(Address address, in UInt256 localIndex, out int globalIndex)
    {
        if (_eventIndices.TryGetValue(address, out int[]? indices) && localIndex < (UInt256)(ulong)indices.Length)
        {
            globalIndex = indices[(int)localIndex.u0];
            return true;
        }
        globalIndex = 0;
        return false;
    }

    /// <summary>Pre-tx code hash for an address whose code changed, memoized across calls in this frame.</summary>
    public ValueHash256 GetPreTxCodeHash(Address address, AccountChangesAtIndex account)
    {
        _preTxCodeHashes ??= [];
        ref ValueHash256 hash = ref CollectionsMarshal.GetValueRefOrAddDefault(_preTxCodeHashes, address, out bool exists);
        if (!exists) hash = ValueKeccak.Compute(account.PreTxCode);
        return hash;
    }

    private static Dictionary<AddressAsKey, int[]> GroupEventIndicesByAddress(LogEntry[] logs)
    {
        Dictionary<AddressAsKey, List<int>> byAddress = [];
        for (int i = 0; i < logs.Length; i++)
        {
            ref List<int>? list = ref CollectionsMarshal.GetValueRefOrAddDefault(byAddress, logs[i].Address, out _);
            (list ??= []).Add(i);
        }
        Dictionary<AddressAsKey, int[]> result = new(byAddress.Count);
        foreach (KeyValuePair<AddressAsKey, List<int>> entry in byAddress) result[entry.Key] = [.. entry.Value];
        return result;
    }

    // A contract deployment is code appearing where the account previously had none (CREATE/CREATE2).
    // EDGE CASES (need EIP-7702 / reference-test confirmation, see PR): a 7702 delegation designator on a
    // fresh EOA also matches this and would surface as a deployment; a redelegation (designator->designator)
    // does not; and a CREATE returning zero-length code records no code change at all.
    private static bool IsDeployment(AccountChangesAtIndex account)
        => account.CodeChange is not null && (account.PreTxCode is null || account.PreTxCode.Length == 0);

    // Ascending uint160: big-endian byte comparison of the 20-byte address matches numeric order.
    private static int CompareAddress(Address a, Address b)
        => a.Bytes.SequenceCompareTo(b.Bytes);
}
