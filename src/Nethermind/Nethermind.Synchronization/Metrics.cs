// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.ComponentModel;
using Nethermind.Core.Attributes;
using Nethermind.Core.Metric;
using Nethermind.Stats.Model;
using Nethermind.Synchronization.SnapSync;

namespace Nethermind.Synchronization
{
    public static class Metrics
    {
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
        [KeyIsLabel("client_type")]
        public static ConcurrentDictionary<NodeClientType, long> SyncPeers { get; set; } = new();

        [GaugeMetric]
        [Description("Number of priority peers.")]
        public static long PriorityPeers;

        [GaugeMetric]
        [Description("State branch progress (percentage of completed branches at second level).")]
        public static long StateBranchProgress;

        [GaugeMetric]
        [Description("Sync time in seconds")]
        public static long SyncTime;

        [DetailedMetric]
        [Description("Snap range result")]
        [KeyIsLabel("is_storage", "result")]
        public static ConcurrentDictionary<SnapRangeResult, long> SnapRangeResult { get; set; } = new();

        [ExponentialPowerHistogramMetric(LabelNames = ["sync_type"], Start = 10, Factor = 10, Count = 5)]
        [Description("Sync dispatcher time in prepare request. High value indicate slow processing in preparing request.")]
        [DetailedMetric]
        public static IMetricObserver SyncDispatcherPrepareRequestTimeMicros = NoopMetricObserver.Instance;

        [ExponentialPowerHistogramMetric(LabelNames = ["sync_type"], Start = 10, Factor = 10, Count = 5)]
        [Description("Sinc dispatcher time in dispatch. High value indicate slow peer or internet.")]
        [DetailedMetric]
        public static IMetricObserver SyncDispatcherDispatchTimeMicros = NoopMetricObserver.Instance;

        [ExponentialPowerHistogramMetric(LabelNames = ["sync_type"], Start = 10, Factor = 10, Count = 5)]
        [Description("Sync dispatcher time in handle. High value indicate slow processing.")]
        [DetailedMetric]
        public static IMetricObserver SyncDispatcherHandleTimeMicros = NoopMetricObserver.Instance;
    }

    public struct SnapRangeResult(bool isStorage, AddRangeResult result) : IMetricLabels
    {
        public string[] Labels => [isStorage ? "true" : "false", result.ToString()];
    }
}
