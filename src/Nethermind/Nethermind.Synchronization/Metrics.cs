// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.ComponentModel;

namespace Nethermind.Synchronization
{
    public static class Metrics
    {
        [Description("Headers downloaded in fast blocks stage")]
        public static decimal FastHeaders;

        [Description("Bodies downloaded in fast blocks stage")]
        public static long FastBodies;

        [Description("Receipts downloaded in fast blocks stage")]
        public static long FastReceipts;

        [Description("State synced in bytes")]
        public static long StateSynced;

        [Description("Requests sent for state nodes sync")]
        public static long StateSyncRequests;

        [Description("State trie nodes synced")]
        public static long SyncedStateTrieNodes;

        [Description("Storage trie nodes synced")]
        public static long SyncedStorageTrieNodes;

        [Description("Synced bytecodes")]
        public static long SyncedCodes;

        [Description("State synced via SNAP Sync in bytes")]
        public static long SnapStateSynced;

        [Description("Synced accounts via SNAP Sync")]
        public static long SnapSyncedAccounts;

        [Description("Synced storage slots via SNAP Sync")]
        public static long SnapSyncedStorageSlots;

        [Description("Synced bytecodes via SNAP Sync")]
        public static long SnapSyncedCodes;

        [Description("Number of sync peers.")]
        public static long SyncPeers;

        [Description("Number of priority peers.")]
        public static long PriorityPeers;

        [Description("State branch progress (percentage of completed branches at second level).")]
        public static long StateBranchProgress;

        [Description("Requests sent for processing by the witness state sync")]
        public static long WitnessStateRequests;

        [Description("Requests sent for processing by the witness block sync")]
        public static long WitnessBlockRequests;
    }
}
