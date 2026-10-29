// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Int256;

namespace Nethermind.Core.Test.BlockAccessLists;

/// <summary>What a <see cref="BlockAccessIndexRecorder"/> recorded for one account: the latest change of each kind.</summary>
/// <remarks>
/// Read from the merge of the recorder's log, so it holds what the block's list would: net changes only, and a slot
/// changed at any index of the log is not among the reads. A log holding several indexes yields each kind's change at
/// the latest index that made one.
/// </remarks>
public sealed class RecordedAccountChanges(Address address)
{
    public Address Address { get; } = address;
    public BalanceChange? BalanceChange { get; private set; }
    public NonceChange? NonceChange { get; private set; }
    public CodeChange? CodeChange { get; private set; }
    public Dictionary<UInt256, StorageChange> StorageChanges { get; } = [];
    public int StorageChangeCount => StorageChanges.Count;
    public HashSet<UInt256> StorageReads { get; } = [];

    public bool HasStorageChange(in UInt256 key) => StorageChanges.ContainsKey(key);

    internal static RecordedAccountChanges From(MergedBlockAccessList.AccountChanges account)
    {
        RecordedAccountChanges recorded = new(account.Address);
        if (!account.BalanceChanges.IsEmpty) recorded.BalanceChange = account.BalanceChanges[^1];
        if (!account.NonceChanges.IsEmpty) recorded.NonceChange = account.NonceChanges[^1];
        if (!account.CodeChanges.IsEmpty) recorded.CodeChange = account.CodeChanges[^1];
        for (int slot = 0; slot < account.SlotCount; slot++) recorded.StorageChanges[account.GetSlotKey(slot)] = account.GetSlotChanges(slot)[^1];
        foreach (UInt256 read in account.StorageReads) recorded.StorageReads.Add(read);
        return recorded;
    }
}

public static class BlockAccessIndexRecorderTestExtensions
{
    extension(BlockAccessIndexRecorder recorder)
    {
        /// <summary>The accounts <paramref name="recorder"/>'s log touched, with what was recorded for them.</summary>
        public IEnumerable<RecordedAccountChanges> AccountChanges
        {
            get
            {
                MergedBlockAccessList merged = recorder.Merge();
                RecordedAccountChanges[] accounts = new RecordedAccountChanges[merged.AccountCount];
                for (int i = 0; i < accounts.Length; i++) accounts[i] = RecordedAccountChanges.From(merged.GetAccount(i));
                return accounts;
            }
        }

        public int AccountCount => recorder.Merge().AccountCount;

        /// <summary>What <paramref name="recorder"/> recorded for <paramref name="address"/>, or null when its log never touched it.</summary>
        public RecordedAccountChanges? GetAccountChanges(Address address)
        {
            foreach (RecordedAccountChanges account in recorder.AccountChanges)
            {
                if (account.Address == address) return account;
            }

            return null;
        }

        public bool HasAccount(Address address) => recorder.GetAccountChanges(address) is not null;

        /// <summary>The block access list of every index in <paramref name="recorder"/>'s log.</summary>
        public GeneratedBlockAccessList ToGeneratedBlockAccessList() => recorder.Merge().ToGeneratedBlockAccessList();

        private MergedBlockAccessList Merge()
        {
            recorder.Log.Seal();
            MergedBlockAccessList merged = new();
            merged.Merge([recorder.Log]);
            return merged;
        }
    }
}
