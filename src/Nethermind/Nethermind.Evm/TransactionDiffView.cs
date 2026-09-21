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

/// <summary>EIP-7906: ordered view of a transaction's state diff and logs, shared by its POST_TX frames.</summary>
/// <remarks>The BAL slice already net-collapses writes and holds the prestate; this adds only the spec's
/// enumeration order and the per-address indexes that make keyed TXDIFF lookups O(1).</remarks>
internal sealed class TransactionDiffView
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
    // Contiguous run within _eventIndices for each emitting address (TXDIFF 0x08/0x09).
    private readonly Dictionary<AddressAsKey, EventRun> _eventRuns;
    // Global Logs indices, in emission order, grouped into one run per emitting address.
    private readonly int[] _eventIndices;
    // Memoized so a warm-priced TXDIFF 0x04 cannot re-hash up to 24 KB of code per call.
    private Dictionary<AddressAsKey, ValueHash256>? _preTxCodeHashes;

    private TransactionDiffView(
        BlockAccessListAtIndex slice,
        Address[] balanceAddresses,
        SlotRef[] slots,
        Address[] deployedAddresses,
        LogEntry[] logs,
        Dictionary<AddressAsKey, (int, int)> slotRuns,
        Dictionary<AddressAsKey, EventRun> eventRuns,
        int[] eventIndices)
    {
        Slice = slice;
        BalanceAddresses = balanceAddresses;
        Slots = slots;
        DeployedAddresses = deployedAddresses;
        Logs = logs;
        _slotRuns = slotRuns;
        _eventRuns = eventRuns;
        _eventIndices = eventIndices;
    }

    public static TransactionDiffView Build(BlockAccessListAtIndex slice, LogEntry[] logs)
    {
        // AccountChanges also holds read-only accesses; filtering first keeps the sort down to the diff.
        List<AccountChangesAtIndex> accounts = new(slice.AccountCount);
        int balanceCount = 0, deployedCount = 0, slotCount = 0;
        foreach (AccountChangesAtIndex account in slice.AccountChanges)
        {
            bool hasBalance = account.BalanceChange is not null;
            bool isDeployment = IsDeployment(account);
            int accountSlots = account.StorageChangeCount;
            if (!hasBalance && accountSlots == 0 && !isDeployment) continue;

            accounts.Add(account);
            if (hasBalance) balanceCount++;
            if (isDeployment) deployedCount++;
            // Exact sizing holds because StorageChangeCount counts the same dictionary the fill pass enumerates.
            slotCount += accountSlots;
        }
        CollectionsMarshal.AsSpan(accounts).Sort(default(AddressOrder));

        Address[] balances = balanceCount == 0 ? [] : new Address[balanceCount];
        Address[] deployed = deployedCount == 0 ? [] : new Address[deployedCount];
        SlotRef[] slots = slotCount == 0 ? [] : new SlotRef[slotCount];
        Dictionary<AddressAsKey, (int, int)> slotRuns = [];
        int balanceAt = 0, deployedAt = 0, slotAt = 0;
        foreach (AccountChangesAtIndex account in accounts)
        {
            if (account.BalanceChange is not null) balances[balanceAt++] = account.Address;
            if (IsDeployment(account)) deployed[deployedAt++] = account.Address;

            int accountSlots = account.StorageChangeCount;
            if (accountSlots > 0)
            {
                int start = slotAt;
                foreach (UInt256 key in account.StorageChanges.Keys) slots[slotAt++] = new SlotRef(account.Address, key);
                // One address per run, so ordering the run by key alone yields the spec's (address, key) order.
                slots.AsSpan(start, accountSlots).Sort(default(SlotKeyOrder));
                slotRuns[account.Address] = (start, accountSlots);
            }
        }

        int[] eventIndices = GroupEventIndicesByAddress(logs, out Dictionary<AddressAsKey, EventRun> eventRuns);
        return new TransactionDiffView(slice, balances, slots, deployed, logs, slotRuns, eventRuns, eventIndices);
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
        => _eventRuns.TryGetValue(address, out EventRun run) ? run.Count : 0;

    public bool TryGetAddressEventGlobalIndex(Address address, in UInt256 localIndex, out int globalIndex)
    {
        if (_eventRuns.TryGetValue(address, out EventRun run) && localIndex < (UInt256)(ulong)run.Count)
        {
            globalIndex = _eventIndices[run.Start + (int)localIndex.u0];
            return true;
        }
        globalIndex = 0;
        return false;
    }

    /// <summary>Pre-tx code hash for an address whose code changed, memoized for the life of this view.</summary>
    public ValueHash256 GetPreTxCodeHash(Address address, AccountChangesAtIndex account)
    {
        _preTxCodeHashes ??= [];
        ref ValueHash256 hash = ref CollectionsMarshal.GetValueRefOrAddDefault(_preTxCodeHashes, address, out bool exists);
        if (!exists) hash = ValueKeccak.Compute(account.PreTxCode);
        return hash;
    }

    /// <summary>Groups the log positions into one contiguous ascending run per emitting address.</summary>
    /// <remarks>Counts first so each run can be placed in a single flat buffer; the second pass claims a
    /// run the first time its address is seen, which keeps each run in emission order.</remarks>
    private static int[] GroupEventIndicesByAddress(LogEntry[] logs, out Dictionary<AddressAsKey, EventRun> runs)
    {
        runs = [];
        if (logs.Length == 0) return [];

        for (int i = 0; i < logs.Length; i++)
        {
            ref EventRun run = ref CollectionsMarshal.GetValueRefOrAddDefault(runs, logs[i].Address, out _);
            run.Count++;
        }

        int[] indices = new int[logs.Length];
        int cursor = 0;
        for (int i = 0; i < logs.Length; i++)
        {
            ref EventRun run = ref CollectionsMarshal.GetValueRefOrNullRef(runs, logs[i].Address);
            if (run.Next == 0)
            {
                run.Start = cursor;
                run.Next = cursor + 1;
                cursor += run.Count;
                indices[run.Start] = i;
            }
            else
            {
                indices[run.Next++] = i;
            }
        }
        return indices;
    }

    // Start is only claimed once per address, so a zero Next unambiguously means "not yet claimed".
    private struct EventRun
    {
        public int Start;
        public int Count;
        public int Next;
    }

    // Spec contracts_deployed: empty code to non-empty, excluding EIP-7702 delegation designators.
    private static bool IsDeployment(AccountChangesAtIndex account)
        => account.CodeChange is { Code: { Length: > 0 } code }
           && (account.PreTxCode is null || account.PreTxCode.Length == 0)
           && !Eip7702Constants.IsDelegatedCode(code);

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
