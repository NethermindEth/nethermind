// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.ComponentModel;
using Nethermind.Core.Attributes;

namespace Nethermind.Synchronization
{
    public static class Metrics
    {
        [GaugeMetric]
        [Description("Is node syncing")]
        public static bool Syncing;

        [GaugeMetric]
        [Description("Headers downloaded in fast blocks stage")]
        public static decimal FastHeaders;

        [GaugeMetric]
        [Description("Bodies downloaded in fast blocks stage")]
        public static long FastBodies;

        [GaugeMetric]
        [Description("Receipts downloaded in fast blocks stage")]
        public static long FastReceipts;

        [GaugeMetric]
        [Description("State synced in bytes")]
        public static long StateSynced;

        [CounterMetric]
        [Description("Requests sent for state nodes sync")]
        public static long StateSyncRequests;

        [CounterMetric]
        [Description("State trie nodes synced")]
        public static long SyncedStateTrieNodes;

        [CounterMetric]
        [Description("Storage trie nodes synced")]
        public static long SyncedStorageTrieNodes;

        [CounterMetric]
        [Description("Synced bytecodes")]
        public static long SyncedCodes;

        [CounterMetric]
        [Description("State synced via SNAP Sync in bytes")]
        public static long SnapStateSynced;

        [CounterMetric]
        [Description("Synced accounts via SNAP Sync")]
        public static long SnapSyncedAccounts;

        [CounterMetric]
        [Description("Synced storage slots via SNAP Sync")]
        public static long SnapSyncedStorageSlots;

        [CounterMetric]
        [Description("Synced bytecodes via SNAP Sync")]
        public static long SnapSyncedCodes;

        [GaugeMetric]
        [Description("Number of sync peers.")]
        public static long SyncPeers;

        [GaugeMetric]
        [Description("Number of priority peers.")]
        public static long PriorityPeers;

        [GaugeMetric]
        [Description("State branch progress (percentage of completed branches at second level).")]
        public static long StateBranchProgress;

        [GaugeMetric]
        [Description("Requests sent for processing by the witness state sync")]
        public static long WitnessStateRequests;

        [GaugeMetric]
        [Description("Requests sent for processing by the witness block sync")]
        public static long WitnessBlockRequests;
    }
}
