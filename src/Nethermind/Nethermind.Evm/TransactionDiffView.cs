// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Evm;

/// <summary>EIP-7906: ordered view of a transaction's state diff and logs, shared by its ASSERT frames.</summary>
/// <remarks>The BAL recorder already net-collapses writes and holds the prestate; this adds only the spec's
/// enumeration order and the per-address indexes that make keyed TXDIFF lookups O(1).</remarks>
internal sealed class TransactionDiffView
{
    public readonly record struct SlotRef(Address Address, UInt256 Key);

    public BlockAccessIndexRecorder Recorder { get; }

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
    private Dictionary<ValueHash256, List<int>>? _topicEvents;

    private TransactionDiffView(
        BlockAccessIndexRecorder recorder,
        Address[] balanceAddresses,
        SlotRef[] slots,
        Address[] deployedAddresses,
        LogEntry[] logs,
        Dictionary<AddressAsKey, (int, int)> slotRuns,
        Dictionary<AddressAsKey, EventRun> eventRuns,
        int[] eventIndices)
    {
        Recorder = recorder;
        BalanceAddresses = balanceAddresses;
        Slots = slots;
        DeployedAddresses = deployedAddresses;
        Logs = logs;
        _slotRuns = slotRuns;
        _eventRuns = eventRuns;
        _eventIndices = eventIndices;
    }

    public static TransactionDiffView Build(BlockAccessIndexRecorder recorder, LogEntry[] logs)
    {
        List<Address> balanceChanges = [];
        List<StorageCell> slotChanges = [];
        List<Address> codeChanges = [];
        recorder.GetChanges(balanceChanges, slotChanges, codeChanges);

        Address[] balances = [.. balanceChanges];
        balances.AsSpan().Sort(default(AddressOrder));
        Address[] deployed = [.. codeChanges.Where(address => IsDeployment(recorder, address))];
        deployed.AsSpan().Sort(default(AddressOrder));
        StorageCell[] cells = [.. slotChanges];
        cells.AsSpan().Sort(default(StorageCellComparer));

        SlotRef[] slots = new SlotRef[cells.Length];
        Dictionary<AddressAsKey, (int, int)> slotRuns = [];
        for (int i = 0; i < cells.Length; i++)
        {
            slots[i] = new SlotRef(cells[i].Address, cells[i].Index);
            ref (int Start, int Count) run = ref CollectionsMarshal.GetValueRefOrAddDefault(slotRuns, cells[i].Address, out bool exists);
            if (!exists) run.Start = i;
            run.Count++;
        }

        int[] eventIndices = GroupEventIndicesByAddress(logs, out Dictionary<AddressAsKey, EventRun> eventRuns);
        return new TransactionDiffView(recorder, balances, slots, deployed, logs, slotRuns, eventRuns, eventIndices);
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

    public int TopicEventCount(in ValueHash256 topic) =>
        TopicEvents.TryGetValue(topic, out List<int>? indices) ? indices.Count : 0;

    public bool TryGetTopicEventGlobalIndex(in ValueHash256 topic, in UInt256 localIndex, out int globalIndex)
    {
        if (TopicEvents.TryGetValue(topic, out List<int>? indices) && localIndex < (UInt256)(ulong)indices.Count)
        {
            globalIndex = indices[(int)localIndex.u0];
            return true;
        }
        globalIndex = 0;
        return false;
    }

    private Dictionary<ValueHash256, List<int>> TopicEvents
    {
        get
        {
            if (_topicEvents is not null) return _topicEvents;
            Dictionary<ValueHash256, List<int>> events = [];
            for (int i = 0; i < Logs.Length; i++)
            {
                Hash256[] topics = Logs[i].Topics;
                for (int j = 1; j < topics.Length; j++)
                {
                    ValueHash256 topic = topics[j].ValueHash256;
                    if (!events.TryGetValue(topic, out List<int>? indices))
                        events.Add(topic, indices = []);
                    if (indices.Count == 0 || indices[^1] != i) indices.Add(i);
                }
            }
            return _topicEvents = events;
        }
    }

    /// <summary>Pre-tx code hash for an address whose code changed, memoized for the life of this view.</summary>
    public ValueHash256 GetPreTxCodeHash(Address address, ReadOnlyMemory<byte> preTxCode)
    {
        _preTxCodeHashes ??= [];
        ref ValueHash256 hash = ref CollectionsMarshal.GetValueRefOrAddDefault(_preTxCodeHashes, address, out bool exists);
        if (!exists) hash = ValueKeccak.Compute(preTxCode.Span);
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
    private static bool IsDeployment(BlockAccessIndexRecorder recorder, Address address)
        => recorder.TryGetCodeChange(address, out CodeChange change)
           && change.Code is { Length: > 0 } code
           && recorder.TryGetPreTxCode(address, out ReadOnlyMemory<byte> preTxCode)
           && preTxCode.IsEmpty
           && !Eip7702Constants.IsDelegatedCode(code);

    // Ascending uint160: big-endian byte comparison of the 20-byte address matches numeric order.
    private readonly struct AddressOrder : IComparer<Address>
    {
        public int Compare(Address? x, Address? y) => x!.Bytes.SequenceCompareTo(y!.Bytes);
    }
}
