// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.ComponentModel;
using Nethermind.Core.Attributes;

namespace Nethermind.StateComposition;

// Partial extension of Metrics that adds the 149 per-depth Prometheus gauges
// and the UpdateFromDistribution helper.  Phase A: populated on scan completion
// only.  Phase B will call this on every incremental diff as well.
//
// Property naming convention note:
//   Nethermind's MetricsController converts PascalCase property names to snake_case
//   using the regex (\p{Ll})(\p{Lu}) — it only inserts an underscore at lowercase→uppercase
//   transitions.  Digits are not treated as word boundaries by this regex, so
//   "Depth7FullNodes" would yield "depth7full_nodes".  To produce the correct
//   "depth_7_full_nodes" form, underscores are embedded directly in the property
//   names: "StateCompAccountTrieDepth_7_FullNodes" → "state_comp_account_trie_depth_7_full_nodes".
public static partial class Metrics
{
    // ------------------------------------------------------------------ //
    // Scalar depth gauges (5)
    // ------------------------------------------------------------------ //

    [GaugeMetric]
    [Description("Weighted average depth of account trie leaf paths")]
    public static double StateCompAvgAccountPathDepth { get; set; }

    [GaugeMetric]
    [Description("Weighted average depth of storage trie leaf paths")]
    public static double StateCompAvgStoragePathDepth { get; set; }

    [GaugeMetric]
    [Description("Deepest occupied level of the account trie")]
    public static long StateCompMaxAccountDepth { get; set; }

    [GaugeMetric]
    [Description("Deepest occupied level across all storage tries (Geth convention: raw depth + 1)")]
    public static long StateCompMaxStorageDepth { get; set; }

    [GaugeMetric]
    [Description("Average children per account-trie branch node")]
    public static double StateCompAvgBranchOccupancy { get; set; }

    // ------------------------------------------------------------------ //
    // Account trie per-depth gauges (64 = 16 depths x 4 fields)
    // Prometheus names: nethermind_state_comp_account_trie_depth_{N}_{field}
    // ------------------------------------------------------------------ //

    [GaugeMetric]
    [Description("Account trie full nodes at depth 0")]
    public static long StateCompAccountTrieDepth_0_FullNodes { get; set; }

    [GaugeMetric]
    [Description("Account trie short nodes at depth 0")]
    public static long StateCompAccountTrieDepth_0_ShortNodes { get; set; }

    [GaugeMetric]
    [Description("Account trie value nodes at depth 0")]
    public static long StateCompAccountTrieDepth_0_ValueNodes { get; set; }

    [GaugeMetric]
    [Description("Account trie bytes at depth 0")]
    public static long StateCompAccountTrieDepth_0_Bytes { get; set; }

    [GaugeMetric]
    [Description("Account trie full nodes at depth 1")]
    public static long StateCompAccountTrieDepth_1_FullNodes { get; set; }

    [GaugeMetric]
    [Description("Account trie short nodes at depth 1")]
    public static long StateCompAccountTrieDepth_1_ShortNodes { get; set; }

    [GaugeMetric]
    [Description("Account trie value nodes at depth 1")]
    public static long StateCompAccountTrieDepth_1_ValueNodes { get; set; }

    [GaugeMetric]
    [Description("Account trie bytes at depth 1")]
    public static long StateCompAccountTrieDepth_1_Bytes { get; set; }

    [GaugeMetric]
    [Description("Account trie full nodes at depth 2")]
    public static long StateCompAccountTrieDepth_2_FullNodes { get; set; }

    [GaugeMetric]
    [Description("Account trie short nodes at depth 2")]
    public static long StateCompAccountTrieDepth_2_ShortNodes { get; set; }

    [GaugeMetric]
    [Description("Account trie value nodes at depth 2")]
    public static long StateCompAccountTrieDepth_2_ValueNodes { get; set; }

    [GaugeMetric]
    [Description("Account trie bytes at depth 2")]
    public static long StateCompAccountTrieDepth_2_Bytes { get; set; }

    [GaugeMetric]
    [Description("Account trie full nodes at depth 3")]
    public static long StateCompAccountTrieDepth_3_FullNodes { get; set; }

    [GaugeMetric]
    [Description("Account trie short nodes at depth 3")]
    public static long StateCompAccountTrieDepth_3_ShortNodes { get; set; }

    [GaugeMetric]
    [Description("Account trie value nodes at depth 3")]
    public static long StateCompAccountTrieDepth_3_ValueNodes { get; set; }

    [GaugeMetric]
    [Description("Account trie bytes at depth 3")]
    public static long StateCompAccountTrieDepth_3_Bytes { get; set; }

    [GaugeMetric]
    [Description("Account trie full nodes at depth 4")]
    public static long StateCompAccountTrieDepth_4_FullNodes { get; set; }

    [GaugeMetric]
    [Description("Account trie short nodes at depth 4")]
    public static long StateCompAccountTrieDepth_4_ShortNodes { get; set; }

    [GaugeMetric]
    [Description("Account trie value nodes at depth 4")]
    public static long StateCompAccountTrieDepth_4_ValueNodes { get; set; }

    [GaugeMetric]
    [Description("Account trie bytes at depth 4")]
    public static long StateCompAccountTrieDepth_4_Bytes { get; set; }

    [GaugeMetric]
    [Description("Account trie full nodes at depth 5")]
    public static long StateCompAccountTrieDepth_5_FullNodes { get; set; }

    [GaugeMetric]
    [Description("Account trie short nodes at depth 5")]
    public static long StateCompAccountTrieDepth_5_ShortNodes { get; set; }

    [GaugeMetric]
    [Description("Account trie value nodes at depth 5")]
    public static long StateCompAccountTrieDepth_5_ValueNodes { get; set; }

    [GaugeMetric]
    [Description("Account trie bytes at depth 5")]
    public static long StateCompAccountTrieDepth_5_Bytes { get; set; }

    [GaugeMetric]
    [Description("Account trie full nodes at depth 6")]
    public static long StateCompAccountTrieDepth_6_FullNodes { get; set; }

    [GaugeMetric]
    [Description("Account trie short nodes at depth 6")]
    public static long StateCompAccountTrieDepth_6_ShortNodes { get; set; }

    [GaugeMetric]
    [Description("Account trie value nodes at depth 6")]
    public static long StateCompAccountTrieDepth_6_ValueNodes { get; set; }

    [GaugeMetric]
    [Description("Account trie bytes at depth 6")]
    public static long StateCompAccountTrieDepth_6_Bytes { get; set; }

    [GaugeMetric]
    [Description("Account trie full nodes at depth 7")]
    public static long StateCompAccountTrieDepth_7_FullNodes { get; set; }

    [GaugeMetric]
    [Description("Account trie short nodes at depth 7")]
    public static long StateCompAccountTrieDepth_7_ShortNodes { get; set; }

    [GaugeMetric]
    [Description("Account trie value nodes at depth 7")]
    public static long StateCompAccountTrieDepth_7_ValueNodes { get; set; }

    [GaugeMetric]
    [Description("Account trie bytes at depth 7")]
    public static long StateCompAccountTrieDepth_7_Bytes { get; set; }

    [GaugeMetric]
    [Description("Account trie full nodes at depth 8")]
    public static long StateCompAccountTrieDepth_8_FullNodes { get; set; }

    [GaugeMetric]
    [Description("Account trie short nodes at depth 8")]
    public static long StateCompAccountTrieDepth_8_ShortNodes { get; set; }

    [GaugeMetric]
    [Description("Account trie value nodes at depth 8")]
    public static long StateCompAccountTrieDepth_8_ValueNodes { get; set; }

    [GaugeMetric]
    [Description("Account trie bytes at depth 8")]
    public static long StateCompAccountTrieDepth_8_Bytes { get; set; }

    [GaugeMetric]
    [Description("Account trie full nodes at depth 9")]
    public static long StateCompAccountTrieDepth_9_FullNodes { get; set; }

    [GaugeMetric]
    [Description("Account trie short nodes at depth 9")]
    public static long StateCompAccountTrieDepth_9_ShortNodes { get; set; }

    [GaugeMetric]
    [Description("Account trie value nodes at depth 9")]
    public static long StateCompAccountTrieDepth_9_ValueNodes { get; set; }

    [GaugeMetric]
    [Description("Account trie bytes at depth 9")]
    public static long StateCompAccountTrieDepth_9_Bytes { get; set; }

    [GaugeMetric]
    [Description("Account trie full nodes at depth 10")]
    public static long StateCompAccountTrieDepth_10_FullNodes { get; set; }

    [GaugeMetric]
    [Description("Account trie short nodes at depth 10")]
    public static long StateCompAccountTrieDepth_10_ShortNodes { get; set; }

    [GaugeMetric]
    [Description("Account trie value nodes at depth 10")]
    public static long StateCompAccountTrieDepth_10_ValueNodes { get; set; }

    [GaugeMetric]
    [Description("Account trie bytes at depth 10")]
    public static long StateCompAccountTrieDepth_10_Bytes { get; set; }

    [GaugeMetric]
    [Description("Account trie full nodes at depth 11")]
    public static long StateCompAccountTrieDepth_11_FullNodes { get; set; }

    [GaugeMetric]
    [Description("Account trie short nodes at depth 11")]
    public static long StateCompAccountTrieDepth_11_ShortNodes { get; set; }

    [GaugeMetric]
    [Description("Account trie value nodes at depth 11")]
    public static long StateCompAccountTrieDepth_11_ValueNodes { get; set; }

    [GaugeMetric]
    [Description("Account trie bytes at depth 11")]
    public static long StateCompAccountTrieDepth_11_Bytes { get; set; }

    [GaugeMetric]
    [Description("Account trie full nodes at depth 12")]
    public static long StateCompAccountTrieDepth_12_FullNodes { get; set; }

    [GaugeMetric]
    [Description("Account trie short nodes at depth 12")]
    public static long StateCompAccountTrieDepth_12_ShortNodes { get; set; }

    [GaugeMetric]
    [Description("Account trie value nodes at depth 12")]
    public static long StateCompAccountTrieDepth_12_ValueNodes { get; set; }

    [GaugeMetric]
    [Description("Account trie bytes at depth 12")]
    public static long StateCompAccountTrieDepth_12_Bytes { get; set; }

    [GaugeMetric]
    [Description("Account trie full nodes at depth 13")]
    public static long StateCompAccountTrieDepth_13_FullNodes { get; set; }

    [GaugeMetric]
    [Description("Account trie short nodes at depth 13")]
    public static long StateCompAccountTrieDepth_13_ShortNodes { get; set; }

    [GaugeMetric]
    [Description("Account trie value nodes at depth 13")]
    public static long StateCompAccountTrieDepth_13_ValueNodes { get; set; }

    [GaugeMetric]
    [Description("Account trie bytes at depth 13")]
    public static long StateCompAccountTrieDepth_13_Bytes { get; set; }

    [GaugeMetric]
    [Description("Account trie full nodes at depth 14")]
    public static long StateCompAccountTrieDepth_14_FullNodes { get; set; }

    [GaugeMetric]
    [Description("Account trie short nodes at depth 14")]
    public static long StateCompAccountTrieDepth_14_ShortNodes { get; set; }

    [GaugeMetric]
    [Description("Account trie value nodes at depth 14")]
    public static long StateCompAccountTrieDepth_14_ValueNodes { get; set; }

    [GaugeMetric]
    [Description("Account trie bytes at depth 14")]
    public static long StateCompAccountTrieDepth_14_Bytes { get; set; }

    [GaugeMetric]
    [Description("Account trie full nodes at depth 15")]
    public static long StateCompAccountTrieDepth_15_FullNodes { get; set; }

    [GaugeMetric]
    [Description("Account trie short nodes at depth 15")]
    public static long StateCompAccountTrieDepth_15_ShortNodes { get; set; }

    [GaugeMetric]
    [Description("Account trie value nodes at depth 15")]
    public static long StateCompAccountTrieDepth_15_ValueNodes { get; set; }

    [GaugeMetric]
    [Description("Account trie bytes at depth 15")]
    public static long StateCompAccountTrieDepth_15_Bytes { get; set; }

    // ------------------------------------------------------------------ //
    // Storage trie per-depth gauges (64 = 16 depths x 4 fields)
    // Prometheus names: nethermind_state_comp_storage_trie_depth_{N}_{field}
    // ------------------------------------------------------------------ //

    [GaugeMetric]
    [Description("Storage trie full nodes at depth 0")]
    public static long StateCompStorageTrieDepth_0_FullNodes { get; set; }

    [GaugeMetric]
    [Description("Storage trie short nodes at depth 0")]
    public static long StateCompStorageTrieDepth_0_ShortNodes { get; set; }

    [GaugeMetric]
    [Description("Storage trie value nodes at depth 0")]
    public static long StateCompStorageTrieDepth_0_ValueNodes { get; set; }

    [GaugeMetric]
    [Description("Storage trie bytes at depth 0")]
    public static long StateCompStorageTrieDepth_0_Bytes { get; set; }

    [GaugeMetric]
    [Description("Storage trie full nodes at depth 1")]
    public static long StateCompStorageTrieDepth_1_FullNodes { get; set; }

    [GaugeMetric]
    [Description("Storage trie short nodes at depth 1")]
    public static long StateCompStorageTrieDepth_1_ShortNodes { get; set; }

    [GaugeMetric]
    [Description("Storage trie value nodes at depth 1")]
    public static long StateCompStorageTrieDepth_1_ValueNodes { get; set; }

    [GaugeMetric]
    [Description("Storage trie bytes at depth 1")]
    public static long StateCompStorageTrieDepth_1_Bytes { get; set; }

    [GaugeMetric]
    [Description("Storage trie full nodes at depth 2")]
    public static long StateCompStorageTrieDepth_2_FullNodes { get; set; }

    [GaugeMetric]
    [Description("Storage trie short nodes at depth 2")]
    public static long StateCompStorageTrieDepth_2_ShortNodes { get; set; }

    [GaugeMetric]
    [Description("Storage trie value nodes at depth 2")]
    public static long StateCompStorageTrieDepth_2_ValueNodes { get; set; }

    [GaugeMetric]
    [Description("Storage trie bytes at depth 2")]
    public static long StateCompStorageTrieDepth_2_Bytes { get; set; }

    [GaugeMetric]
    [Description("Storage trie full nodes at depth 3")]
    public static long StateCompStorageTrieDepth_3_FullNodes { get; set; }

    [GaugeMetric]
    [Description("Storage trie short nodes at depth 3")]
    public static long StateCompStorageTrieDepth_3_ShortNodes { get; set; }

    [GaugeMetric]
    [Description("Storage trie value nodes at depth 3")]
    public static long StateCompStorageTrieDepth_3_ValueNodes { get; set; }

    [GaugeMetric]
    [Description("Storage trie bytes at depth 3")]
    public static long StateCompStorageTrieDepth_3_Bytes { get; set; }

    [GaugeMetric]
    [Description("Storage trie full nodes at depth 4")]
    public static long StateCompStorageTrieDepth_4_FullNodes { get; set; }

    [GaugeMetric]
    [Description("Storage trie short nodes at depth 4")]
    public static long StateCompStorageTrieDepth_4_ShortNodes { get; set; }

    [GaugeMetric]
    [Description("Storage trie value nodes at depth 4")]
    public static long StateCompStorageTrieDepth_4_ValueNodes { get; set; }

    [GaugeMetric]
    [Description("Storage trie bytes at depth 4")]
    public static long StateCompStorageTrieDepth_4_Bytes { get; set; }

    [GaugeMetric]
    [Description("Storage trie full nodes at depth 5")]
    public static long StateCompStorageTrieDepth_5_FullNodes { get; set; }

    [GaugeMetric]
    [Description("Storage trie short nodes at depth 5")]
    public static long StateCompStorageTrieDepth_5_ShortNodes { get; set; }

    [GaugeMetric]
    [Description("Storage trie value nodes at depth 5")]
    public static long StateCompStorageTrieDepth_5_ValueNodes { get; set; }

    [GaugeMetric]
    [Description("Storage trie bytes at depth 5")]
    public static long StateCompStorageTrieDepth_5_Bytes { get; set; }

    [GaugeMetric]
    [Description("Storage trie full nodes at depth 6")]
    public static long StateCompStorageTrieDepth_6_FullNodes { get; set; }

    [GaugeMetric]
    [Description("Storage trie short nodes at depth 6")]
    public static long StateCompStorageTrieDepth_6_ShortNodes { get; set; }

    [GaugeMetric]
    [Description("Storage trie value nodes at depth 6")]
    public static long StateCompStorageTrieDepth_6_ValueNodes { get; set; }

    [GaugeMetric]
    [Description("Storage trie bytes at depth 6")]
    public static long StateCompStorageTrieDepth_6_Bytes { get; set; }

    [GaugeMetric]
    [Description("Storage trie full nodes at depth 7")]
    public static long StateCompStorageTrieDepth_7_FullNodes { get; set; }

    [GaugeMetric]
    [Description("Storage trie short nodes at depth 7")]
    public static long StateCompStorageTrieDepth_7_ShortNodes { get; set; }

    [GaugeMetric]
    [Description("Storage trie value nodes at depth 7")]
    public static long StateCompStorageTrieDepth_7_ValueNodes { get; set; }

    [GaugeMetric]
    [Description("Storage trie bytes at depth 7")]
    public static long StateCompStorageTrieDepth_7_Bytes { get; set; }

    [GaugeMetric]
    [Description("Storage trie full nodes at depth 8")]
    public static long StateCompStorageTrieDepth_8_FullNodes { get; set; }

    [GaugeMetric]
    [Description("Storage trie short nodes at depth 8")]
    public static long StateCompStorageTrieDepth_8_ShortNodes { get; set; }

    [GaugeMetric]
    [Description("Storage trie value nodes at depth 8")]
    public static long StateCompStorageTrieDepth_8_ValueNodes { get; set; }

    [GaugeMetric]
    [Description("Storage trie bytes at depth 8")]
    public static long StateCompStorageTrieDepth_8_Bytes { get; set; }

    [GaugeMetric]
    [Description("Storage trie full nodes at depth 9")]
    public static long StateCompStorageTrieDepth_9_FullNodes { get; set; }

    [GaugeMetric]
    [Description("Storage trie short nodes at depth 9")]
    public static long StateCompStorageTrieDepth_9_ShortNodes { get; set; }

    [GaugeMetric]
    [Description("Storage trie value nodes at depth 9")]
    public static long StateCompStorageTrieDepth_9_ValueNodes { get; set; }

    [GaugeMetric]
    [Description("Storage trie bytes at depth 9")]
    public static long StateCompStorageTrieDepth_9_Bytes { get; set; }

    [GaugeMetric]
    [Description("Storage trie full nodes at depth 10")]
    public static long StateCompStorageTrieDepth_10_FullNodes { get; set; }

    [GaugeMetric]
    [Description("Storage trie short nodes at depth 10")]
    public static long StateCompStorageTrieDepth_10_ShortNodes { get; set; }

    [GaugeMetric]
    [Description("Storage trie value nodes at depth 10")]
    public static long StateCompStorageTrieDepth_10_ValueNodes { get; set; }

    [GaugeMetric]
    [Description("Storage trie bytes at depth 10")]
    public static long StateCompStorageTrieDepth_10_Bytes { get; set; }

    [GaugeMetric]
    [Description("Storage trie full nodes at depth 11")]
    public static long StateCompStorageTrieDepth_11_FullNodes { get; set; }

    [GaugeMetric]
    [Description("Storage trie short nodes at depth 11")]
    public static long StateCompStorageTrieDepth_11_ShortNodes { get; set; }

    [GaugeMetric]
    [Description("Storage trie value nodes at depth 11")]
    public static long StateCompStorageTrieDepth_11_ValueNodes { get; set; }

    [GaugeMetric]
    [Description("Storage trie bytes at depth 11")]
    public static long StateCompStorageTrieDepth_11_Bytes { get; set; }

    [GaugeMetric]
    [Description("Storage trie full nodes at depth 12")]
    public static long StateCompStorageTrieDepth_12_FullNodes { get; set; }

    [GaugeMetric]
    [Description("Storage trie short nodes at depth 12")]
    public static long StateCompStorageTrieDepth_12_ShortNodes { get; set; }

    [GaugeMetric]
    [Description("Storage trie value nodes at depth 12")]
    public static long StateCompStorageTrieDepth_12_ValueNodes { get; set; }

    [GaugeMetric]
    [Description("Storage trie bytes at depth 12")]
    public static long StateCompStorageTrieDepth_12_Bytes { get; set; }

    [GaugeMetric]
    [Description("Storage trie full nodes at depth 13")]
    public static long StateCompStorageTrieDepth_13_FullNodes { get; set; }

    [GaugeMetric]
    [Description("Storage trie short nodes at depth 13")]
    public static long StateCompStorageTrieDepth_13_ShortNodes { get; set; }

    [GaugeMetric]
    [Description("Storage trie value nodes at depth 13")]
    public static long StateCompStorageTrieDepth_13_ValueNodes { get; set; }

    [GaugeMetric]
    [Description("Storage trie bytes at depth 13")]
    public static long StateCompStorageTrieDepth_13_Bytes { get; set; }

    [GaugeMetric]
    [Description("Storage trie full nodes at depth 14")]
    public static long StateCompStorageTrieDepth_14_FullNodes { get; set; }

    [GaugeMetric]
    [Description("Storage trie short nodes at depth 14")]
    public static long StateCompStorageTrieDepth_14_ShortNodes { get; set; }

    [GaugeMetric]
    [Description("Storage trie value nodes at depth 14")]
    public static long StateCompStorageTrieDepth_14_ValueNodes { get; set; }

    [GaugeMetric]
    [Description("Storage trie bytes at depth 14")]
    public static long StateCompStorageTrieDepth_14_Bytes { get; set; }

    [GaugeMetric]
    [Description("Storage trie full nodes at depth 15")]
    public static long StateCompStorageTrieDepth_15_FullNodes { get; set; }

    [GaugeMetric]
    [Description("Storage trie short nodes at depth 15")]
    public static long StateCompStorageTrieDepth_15_ShortNodes { get; set; }

    [GaugeMetric]
    [Description("Storage trie value nodes at depth 15")]
    public static long StateCompStorageTrieDepth_15_ValueNodes { get; set; }

    [GaugeMetric]
    [Description("Storage trie bytes at depth 15")]
    public static long StateCompStorageTrieDepth_15_Bytes { get; set; }

    // ------------------------------------------------------------------ //
    // Branch occupancy histogram (16 buckets, C = 1..16 children)
    // Prometheus names: nethermind_state_comp_account_trie_branch_occupancy_{C}_children
    // ------------------------------------------------------------------ //

    [GaugeMetric]
    [Description("Account trie branches with exactly 1 child")]
    public static long StateCompAccountTrieBranchOccupancy_1_Children { get; set; }

    [GaugeMetric]
    [Description("Account trie branches with exactly 2 children")]
    public static long StateCompAccountTrieBranchOccupancy_2_Children { get; set; }

    [GaugeMetric]
    [Description("Account trie branches with exactly 3 children")]
    public static long StateCompAccountTrieBranchOccupancy_3_Children { get; set; }

    [GaugeMetric]
    [Description("Account trie branches with exactly 4 children")]
    public static long StateCompAccountTrieBranchOccupancy_4_Children { get; set; }

    [GaugeMetric]
    [Description("Account trie branches with exactly 5 children")]
    public static long StateCompAccountTrieBranchOccupancy_5_Children { get; set; }

    [GaugeMetric]
    [Description("Account trie branches with exactly 6 children")]
    public static long StateCompAccountTrieBranchOccupancy_6_Children { get; set; }

    [GaugeMetric]
    [Description("Account trie branches with exactly 7 children")]
    public static long StateCompAccountTrieBranchOccupancy_7_Children { get; set; }

    [GaugeMetric]
    [Description("Account trie branches with exactly 8 children")]
    public static long StateCompAccountTrieBranchOccupancy_8_Children { get; set; }

    [GaugeMetric]
    [Description("Account trie branches with exactly 9 children")]
    public static long StateCompAccountTrieBranchOccupancy_9_Children { get; set; }

    [GaugeMetric]
    [Description("Account trie branches with exactly 10 children")]
    public static long StateCompAccountTrieBranchOccupancy_10_Children { get; set; }

    [GaugeMetric]
    [Description("Account trie branches with exactly 11 children")]
    public static long StateCompAccountTrieBranchOccupancy_11_Children { get; set; }

    [GaugeMetric]
    [Description("Account trie branches with exactly 12 children")]
    public static long StateCompAccountTrieBranchOccupancy_12_Children { get; set; }

    [GaugeMetric]
    [Description("Account trie branches with exactly 13 children")]
    public static long StateCompAccountTrieBranchOccupancy_13_Children { get; set; }

    [GaugeMetric]
    [Description("Account trie branches with exactly 14 children")]
    public static long StateCompAccountTrieBranchOccupancy_14_Children { get; set; }

    [GaugeMetric]
    [Description("Account trie branches with exactly 15 children")]
    public static long StateCompAccountTrieBranchOccupancy_15_Children { get; set; }

    [GaugeMetric]
    [Description("Account trie branches with exactly 16 children")]
    public static long StateCompAccountTrieBranchOccupancy_16_Children { get; set; }

    // ------------------------------------------------------------------ //
    // UpdateFromDistribution — called on scan completion (Phase A).
    // Phase B will also call this after every incremental diff.
    // No reflection; uses a switch dispatch so the JIT can devirtualise.
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Populate all 149 depth-distribution gauges from a <see cref="TrieDepthDistribution"/>
    /// produced by a completed full scan.  Depths not present in the distribution arrays
    /// are explicitly zeroed so that a second call with different data cannot leak stale values.
    /// </summary>
    public static void UpdateFromDistribution(TrieDepthDistribution dist)
    {
        // Zero all per-depth account trie gauges before writing fresh values.
        StateCompAccountTrieDepth_0_FullNodes  = StateCompAccountTrieDepth_0_ShortNodes  = StateCompAccountTrieDepth_0_ValueNodes  = StateCompAccountTrieDepth_0_Bytes  = 0;
        StateCompAccountTrieDepth_1_FullNodes  = StateCompAccountTrieDepth_1_ShortNodes  = StateCompAccountTrieDepth_1_ValueNodes  = StateCompAccountTrieDepth_1_Bytes  = 0;
        StateCompAccountTrieDepth_2_FullNodes  = StateCompAccountTrieDepth_2_ShortNodes  = StateCompAccountTrieDepth_2_ValueNodes  = StateCompAccountTrieDepth_2_Bytes  = 0;
        StateCompAccountTrieDepth_3_FullNodes  = StateCompAccountTrieDepth_3_ShortNodes  = StateCompAccountTrieDepth_3_ValueNodes  = StateCompAccountTrieDepth_3_Bytes  = 0;
        StateCompAccountTrieDepth_4_FullNodes  = StateCompAccountTrieDepth_4_ShortNodes  = StateCompAccountTrieDepth_4_ValueNodes  = StateCompAccountTrieDepth_4_Bytes  = 0;
        StateCompAccountTrieDepth_5_FullNodes  = StateCompAccountTrieDepth_5_ShortNodes  = StateCompAccountTrieDepth_5_ValueNodes  = StateCompAccountTrieDepth_5_Bytes  = 0;
        StateCompAccountTrieDepth_6_FullNodes  = StateCompAccountTrieDepth_6_ShortNodes  = StateCompAccountTrieDepth_6_ValueNodes  = StateCompAccountTrieDepth_6_Bytes  = 0;
        StateCompAccountTrieDepth_7_FullNodes  = StateCompAccountTrieDepth_7_ShortNodes  = StateCompAccountTrieDepth_7_ValueNodes  = StateCompAccountTrieDepth_7_Bytes  = 0;
        StateCompAccountTrieDepth_8_FullNodes  = StateCompAccountTrieDepth_8_ShortNodes  = StateCompAccountTrieDepth_8_ValueNodes  = StateCompAccountTrieDepth_8_Bytes  = 0;
        StateCompAccountTrieDepth_9_FullNodes  = StateCompAccountTrieDepth_9_ShortNodes  = StateCompAccountTrieDepth_9_ValueNodes  = StateCompAccountTrieDepth_9_Bytes  = 0;
        StateCompAccountTrieDepth_10_FullNodes = StateCompAccountTrieDepth_10_ShortNodes = StateCompAccountTrieDepth_10_ValueNodes = StateCompAccountTrieDepth_10_Bytes = 0;
        StateCompAccountTrieDepth_11_FullNodes = StateCompAccountTrieDepth_11_ShortNodes = StateCompAccountTrieDepth_11_ValueNodes = StateCompAccountTrieDepth_11_Bytes = 0;
        StateCompAccountTrieDepth_12_FullNodes = StateCompAccountTrieDepth_12_ShortNodes = StateCompAccountTrieDepth_12_ValueNodes = StateCompAccountTrieDepth_12_Bytes = 0;
        StateCompAccountTrieDepth_13_FullNodes = StateCompAccountTrieDepth_13_ShortNodes = StateCompAccountTrieDepth_13_ValueNodes = StateCompAccountTrieDepth_13_Bytes = 0;
        StateCompAccountTrieDepth_14_FullNodes = StateCompAccountTrieDepth_14_ShortNodes = StateCompAccountTrieDepth_14_ValueNodes = StateCompAccountTrieDepth_14_Bytes = 0;
        StateCompAccountTrieDepth_15_FullNodes = StateCompAccountTrieDepth_15_ShortNodes = StateCompAccountTrieDepth_15_ValueNodes = StateCompAccountTrieDepth_15_Bytes = 0;

        StateCompStorageTrieDepth_0_FullNodes  = StateCompStorageTrieDepth_0_ShortNodes  = StateCompStorageTrieDepth_0_ValueNodes  = StateCompStorageTrieDepth_0_Bytes  = 0;
        StateCompStorageTrieDepth_1_FullNodes  = StateCompStorageTrieDepth_1_ShortNodes  = StateCompStorageTrieDepth_1_ValueNodes  = StateCompStorageTrieDepth_1_Bytes  = 0;
        StateCompStorageTrieDepth_2_FullNodes  = StateCompStorageTrieDepth_2_ShortNodes  = StateCompStorageTrieDepth_2_ValueNodes  = StateCompStorageTrieDepth_2_Bytes  = 0;
        StateCompStorageTrieDepth_3_FullNodes  = StateCompStorageTrieDepth_3_ShortNodes  = StateCompStorageTrieDepth_3_ValueNodes  = StateCompStorageTrieDepth_3_Bytes  = 0;
        StateCompStorageTrieDepth_4_FullNodes  = StateCompStorageTrieDepth_4_ShortNodes  = StateCompStorageTrieDepth_4_ValueNodes  = StateCompStorageTrieDepth_4_Bytes  = 0;
        StateCompStorageTrieDepth_5_FullNodes  = StateCompStorageTrieDepth_5_ShortNodes  = StateCompStorageTrieDepth_5_ValueNodes  = StateCompStorageTrieDepth_5_Bytes  = 0;
        StateCompStorageTrieDepth_6_FullNodes  = StateCompStorageTrieDepth_6_ShortNodes  = StateCompStorageTrieDepth_6_ValueNodes  = StateCompStorageTrieDepth_6_Bytes  = 0;
        StateCompStorageTrieDepth_7_FullNodes  = StateCompStorageTrieDepth_7_ShortNodes  = StateCompStorageTrieDepth_7_ValueNodes  = StateCompStorageTrieDepth_7_Bytes  = 0;
        StateCompStorageTrieDepth_8_FullNodes  = StateCompStorageTrieDepth_8_ShortNodes  = StateCompStorageTrieDepth_8_ValueNodes  = StateCompStorageTrieDepth_8_Bytes  = 0;
        StateCompStorageTrieDepth_9_FullNodes  = StateCompStorageTrieDepth_9_ShortNodes  = StateCompStorageTrieDepth_9_ValueNodes  = StateCompStorageTrieDepth_9_Bytes  = 0;
        StateCompStorageTrieDepth_10_FullNodes = StateCompStorageTrieDepth_10_ShortNodes = StateCompStorageTrieDepth_10_ValueNodes = StateCompStorageTrieDepth_10_Bytes = 0;
        StateCompStorageTrieDepth_11_FullNodes = StateCompStorageTrieDepth_11_ShortNodes = StateCompStorageTrieDepth_11_ValueNodes = StateCompStorageTrieDepth_11_Bytes = 0;
        StateCompStorageTrieDepth_12_FullNodes = StateCompStorageTrieDepth_12_ShortNodes = StateCompStorageTrieDepth_12_ValueNodes = StateCompStorageTrieDepth_12_Bytes = 0;
        StateCompStorageTrieDepth_13_FullNodes = StateCompStorageTrieDepth_13_ShortNodes = StateCompStorageTrieDepth_13_ValueNodes = StateCompStorageTrieDepth_13_Bytes = 0;
        StateCompStorageTrieDepth_14_FullNodes = StateCompStorageTrieDepth_14_ShortNodes = StateCompStorageTrieDepth_14_ValueNodes = StateCompStorageTrieDepth_14_Bytes = 0;
        StateCompStorageTrieDepth_15_FullNodes = StateCompStorageTrieDepth_15_ShortNodes = StateCompStorageTrieDepth_15_ValueNodes = StateCompStorageTrieDepth_15_Bytes = 0;

        StateCompAccountTrieBranchOccupancy_1_Children  = 0;
        StateCompAccountTrieBranchOccupancy_2_Children  = 0;
        StateCompAccountTrieBranchOccupancy_3_Children  = 0;
        StateCompAccountTrieBranchOccupancy_4_Children  = 0;
        StateCompAccountTrieBranchOccupancy_5_Children  = 0;
        StateCompAccountTrieBranchOccupancy_6_Children  = 0;
        StateCompAccountTrieBranchOccupancy_7_Children  = 0;
        StateCompAccountTrieBranchOccupancy_8_Children  = 0;
        StateCompAccountTrieBranchOccupancy_9_Children  = 0;
        StateCompAccountTrieBranchOccupancy_10_Children = 0;
        StateCompAccountTrieBranchOccupancy_11_Children = 0;
        StateCompAccountTrieBranchOccupancy_12_Children = 0;
        StateCompAccountTrieBranchOccupancy_13_Children = 0;
        StateCompAccountTrieBranchOccupancy_14_Children = 0;
        StateCompAccountTrieBranchOccupancy_15_Children = 0;
        StateCompAccountTrieBranchOccupancy_16_Children = 0;

        foreach (TrieLevelStat stat in dist.AccountTrieLevels)
        {
            int d = stat.Depth < 16 ? stat.Depth : 15;
            switch (d)
            {
                case 0:
                    StateCompAccountTrieDepth_0_FullNodes  = stat.FullNodeCount;
                    StateCompAccountTrieDepth_0_ShortNodes = stat.ShortNodeCount;
                    StateCompAccountTrieDepth_0_ValueNodes = stat.ValueNodeCount;
                    StateCompAccountTrieDepth_0_Bytes      = stat.TotalSize;
                    break;
                case 1:
                    StateCompAccountTrieDepth_1_FullNodes  = stat.FullNodeCount;
                    StateCompAccountTrieDepth_1_ShortNodes = stat.ShortNodeCount;
                    StateCompAccountTrieDepth_1_ValueNodes = stat.ValueNodeCount;
                    StateCompAccountTrieDepth_1_Bytes      = stat.TotalSize;
                    break;
                case 2:
                    StateCompAccountTrieDepth_2_FullNodes  = stat.FullNodeCount;
                    StateCompAccountTrieDepth_2_ShortNodes = stat.ShortNodeCount;
                    StateCompAccountTrieDepth_2_ValueNodes = stat.ValueNodeCount;
                    StateCompAccountTrieDepth_2_Bytes      = stat.TotalSize;
                    break;
                case 3:
                    StateCompAccountTrieDepth_3_FullNodes  = stat.FullNodeCount;
                    StateCompAccountTrieDepth_3_ShortNodes = stat.ShortNodeCount;
                    StateCompAccountTrieDepth_3_ValueNodes = stat.ValueNodeCount;
                    StateCompAccountTrieDepth_3_Bytes      = stat.TotalSize;
                    break;
                case 4:
                    StateCompAccountTrieDepth_4_FullNodes  = stat.FullNodeCount;
                    StateCompAccountTrieDepth_4_ShortNodes = stat.ShortNodeCount;
                    StateCompAccountTrieDepth_4_ValueNodes = stat.ValueNodeCount;
                    StateCompAccountTrieDepth_4_Bytes      = stat.TotalSize;
                    break;
                case 5:
                    StateCompAccountTrieDepth_5_FullNodes  = stat.FullNodeCount;
                    StateCompAccountTrieDepth_5_ShortNodes = stat.ShortNodeCount;
                    StateCompAccountTrieDepth_5_ValueNodes = stat.ValueNodeCount;
                    StateCompAccountTrieDepth_5_Bytes      = stat.TotalSize;
                    break;
                case 6:
                    StateCompAccountTrieDepth_6_FullNodes  = stat.FullNodeCount;
                    StateCompAccountTrieDepth_6_ShortNodes = stat.ShortNodeCount;
                    StateCompAccountTrieDepth_6_ValueNodes = stat.ValueNodeCount;
                    StateCompAccountTrieDepth_6_Bytes      = stat.TotalSize;
                    break;
                case 7:
                    StateCompAccountTrieDepth_7_FullNodes  = stat.FullNodeCount;
                    StateCompAccountTrieDepth_7_ShortNodes = stat.ShortNodeCount;
                    StateCompAccountTrieDepth_7_ValueNodes = stat.ValueNodeCount;
                    StateCompAccountTrieDepth_7_Bytes      = stat.TotalSize;
                    break;
                case 8:
                    StateCompAccountTrieDepth_8_FullNodes  = stat.FullNodeCount;
                    StateCompAccountTrieDepth_8_ShortNodes = stat.ShortNodeCount;
                    StateCompAccountTrieDepth_8_ValueNodes = stat.ValueNodeCount;
                    StateCompAccountTrieDepth_8_Bytes      = stat.TotalSize;
                    break;
                case 9:
                    StateCompAccountTrieDepth_9_FullNodes  = stat.FullNodeCount;
                    StateCompAccountTrieDepth_9_ShortNodes = stat.ShortNodeCount;
                    StateCompAccountTrieDepth_9_ValueNodes = stat.ValueNodeCount;
                    StateCompAccountTrieDepth_9_Bytes      = stat.TotalSize;
                    break;
                case 10:
                    StateCompAccountTrieDepth_10_FullNodes  = stat.FullNodeCount;
                    StateCompAccountTrieDepth_10_ShortNodes = stat.ShortNodeCount;
                    StateCompAccountTrieDepth_10_ValueNodes = stat.ValueNodeCount;
                    StateCompAccountTrieDepth_10_Bytes      = stat.TotalSize;
                    break;
                case 11:
                    StateCompAccountTrieDepth_11_FullNodes  = stat.FullNodeCount;
                    StateCompAccountTrieDepth_11_ShortNodes = stat.ShortNodeCount;
                    StateCompAccountTrieDepth_11_ValueNodes = stat.ValueNodeCount;
                    StateCompAccountTrieDepth_11_Bytes      = stat.TotalSize;
                    break;
                case 12:
                    StateCompAccountTrieDepth_12_FullNodes  = stat.FullNodeCount;
                    StateCompAccountTrieDepth_12_ShortNodes = stat.ShortNodeCount;
                    StateCompAccountTrieDepth_12_ValueNodes = stat.ValueNodeCount;
                    StateCompAccountTrieDepth_12_Bytes      = stat.TotalSize;
                    break;
                case 13:
                    StateCompAccountTrieDepth_13_FullNodes  = stat.FullNodeCount;
                    StateCompAccountTrieDepth_13_ShortNodes = stat.ShortNodeCount;
                    StateCompAccountTrieDepth_13_ValueNodes = stat.ValueNodeCount;
                    StateCompAccountTrieDepth_13_Bytes      = stat.TotalSize;
                    break;
                case 14:
                    StateCompAccountTrieDepth_14_FullNodes  = stat.FullNodeCount;
                    StateCompAccountTrieDepth_14_ShortNodes = stat.ShortNodeCount;
                    StateCompAccountTrieDepth_14_ValueNodes = stat.ValueNodeCount;
                    StateCompAccountTrieDepth_14_Bytes      = stat.TotalSize;
                    break;
                case 15:
                    StateCompAccountTrieDepth_15_FullNodes  = stat.FullNodeCount;
                    StateCompAccountTrieDepth_15_ShortNodes = stat.ShortNodeCount;
                    StateCompAccountTrieDepth_15_ValueNodes = stat.ValueNodeCount;
                    StateCompAccountTrieDepth_15_Bytes      = stat.TotalSize;
                    break;
            }
        }

        foreach (TrieLevelStat stat in dist.StorageTrieLevels)
        {
            int d = stat.Depth < 16 ? stat.Depth : 15;
            switch (d)
            {
                case 0:
                    StateCompStorageTrieDepth_0_FullNodes  = stat.FullNodeCount;
                    StateCompStorageTrieDepth_0_ShortNodes = stat.ShortNodeCount;
                    StateCompStorageTrieDepth_0_ValueNodes = stat.ValueNodeCount;
                    StateCompStorageTrieDepth_0_Bytes      = stat.TotalSize;
                    break;
                case 1:
                    StateCompStorageTrieDepth_1_FullNodes  = stat.FullNodeCount;
                    StateCompStorageTrieDepth_1_ShortNodes = stat.ShortNodeCount;
                    StateCompStorageTrieDepth_1_ValueNodes = stat.ValueNodeCount;
                    StateCompStorageTrieDepth_1_Bytes      = stat.TotalSize;
                    break;
                case 2:
                    StateCompStorageTrieDepth_2_FullNodes  = stat.FullNodeCount;
                    StateCompStorageTrieDepth_2_ShortNodes = stat.ShortNodeCount;
                    StateCompStorageTrieDepth_2_ValueNodes = stat.ValueNodeCount;
                    StateCompStorageTrieDepth_2_Bytes      = stat.TotalSize;
                    break;
                case 3:
                    StateCompStorageTrieDepth_3_FullNodes  = stat.FullNodeCount;
                    StateCompStorageTrieDepth_3_ShortNodes = stat.ShortNodeCount;
                    StateCompStorageTrieDepth_3_ValueNodes = stat.ValueNodeCount;
                    StateCompStorageTrieDepth_3_Bytes      = stat.TotalSize;
                    break;
                case 4:
                    StateCompStorageTrieDepth_4_FullNodes  = stat.FullNodeCount;
                    StateCompStorageTrieDepth_4_ShortNodes = stat.ShortNodeCount;
                    StateCompStorageTrieDepth_4_ValueNodes = stat.ValueNodeCount;
                    StateCompStorageTrieDepth_4_Bytes      = stat.TotalSize;
                    break;
                case 5:
                    StateCompStorageTrieDepth_5_FullNodes  = stat.FullNodeCount;
                    StateCompStorageTrieDepth_5_ShortNodes = stat.ShortNodeCount;
                    StateCompStorageTrieDepth_5_ValueNodes = stat.ValueNodeCount;
                    StateCompStorageTrieDepth_5_Bytes      = stat.TotalSize;
                    break;
                case 6:
                    StateCompStorageTrieDepth_6_FullNodes  = stat.FullNodeCount;
                    StateCompStorageTrieDepth_6_ShortNodes = stat.ShortNodeCount;
                    StateCompStorageTrieDepth_6_ValueNodes = stat.ValueNodeCount;
                    StateCompStorageTrieDepth_6_Bytes      = stat.TotalSize;
                    break;
                case 7:
                    StateCompStorageTrieDepth_7_FullNodes  = stat.FullNodeCount;
                    StateCompStorageTrieDepth_7_ShortNodes = stat.ShortNodeCount;
                    StateCompStorageTrieDepth_7_ValueNodes = stat.ValueNodeCount;
                    StateCompStorageTrieDepth_7_Bytes      = stat.TotalSize;
                    break;
                case 8:
                    StateCompStorageTrieDepth_8_FullNodes  = stat.FullNodeCount;
                    StateCompStorageTrieDepth_8_ShortNodes = stat.ShortNodeCount;
                    StateCompStorageTrieDepth_8_ValueNodes = stat.ValueNodeCount;
                    StateCompStorageTrieDepth_8_Bytes      = stat.TotalSize;
                    break;
                case 9:
                    StateCompStorageTrieDepth_9_FullNodes  = stat.FullNodeCount;
                    StateCompStorageTrieDepth_9_ShortNodes = stat.ShortNodeCount;
                    StateCompStorageTrieDepth_9_ValueNodes = stat.ValueNodeCount;
                    StateCompStorageTrieDepth_9_Bytes      = stat.TotalSize;
                    break;
                case 10:
                    StateCompStorageTrieDepth_10_FullNodes  = stat.FullNodeCount;
                    StateCompStorageTrieDepth_10_ShortNodes = stat.ShortNodeCount;
                    StateCompStorageTrieDepth_10_ValueNodes = stat.ValueNodeCount;
                    StateCompStorageTrieDepth_10_Bytes      = stat.TotalSize;
                    break;
                case 11:
                    StateCompStorageTrieDepth_11_FullNodes  = stat.FullNodeCount;
                    StateCompStorageTrieDepth_11_ShortNodes = stat.ShortNodeCount;
                    StateCompStorageTrieDepth_11_ValueNodes = stat.ValueNodeCount;
                    StateCompStorageTrieDepth_11_Bytes      = stat.TotalSize;
                    break;
                case 12:
                    StateCompStorageTrieDepth_12_FullNodes  = stat.FullNodeCount;
                    StateCompStorageTrieDepth_12_ShortNodes = stat.ShortNodeCount;
                    StateCompStorageTrieDepth_12_ValueNodes = stat.ValueNodeCount;
                    StateCompStorageTrieDepth_12_Bytes      = stat.TotalSize;
                    break;
                case 13:
                    StateCompStorageTrieDepth_13_FullNodes  = stat.FullNodeCount;
                    StateCompStorageTrieDepth_13_ShortNodes = stat.ShortNodeCount;
                    StateCompStorageTrieDepth_13_ValueNodes = stat.ValueNodeCount;
                    StateCompStorageTrieDepth_13_Bytes      = stat.TotalSize;
                    break;
                case 14:
                    StateCompStorageTrieDepth_14_FullNodes  = stat.FullNodeCount;
                    StateCompStorageTrieDepth_14_ShortNodes = stat.ShortNodeCount;
                    StateCompStorageTrieDepth_14_ValueNodes = stat.ValueNodeCount;
                    StateCompStorageTrieDepth_14_Bytes      = stat.TotalSize;
                    break;
                case 15:
                    StateCompStorageTrieDepth_15_FullNodes  = stat.FullNodeCount;
                    StateCompStorageTrieDepth_15_ShortNodes = stat.ShortNodeCount;
                    StateCompStorageTrieDepth_15_ValueNodes = stat.ValueNodeCount;
                    StateCompStorageTrieDepth_15_Bytes      = stat.TotalSize;
                    break;
            }
        }

        for (int i = 0; i < dist.BranchOccupancyDistribution.Length && i < 16; i++)
        {
            switch (i + 1)
            {
                case 1:  StateCompAccountTrieBranchOccupancy_1_Children  = dist.BranchOccupancyDistribution[0];  break;
                case 2:  StateCompAccountTrieBranchOccupancy_2_Children  = dist.BranchOccupancyDistribution[1];  break;
                case 3:  StateCompAccountTrieBranchOccupancy_3_Children  = dist.BranchOccupancyDistribution[2];  break;
                case 4:  StateCompAccountTrieBranchOccupancy_4_Children  = dist.BranchOccupancyDistribution[3];  break;
                case 5:  StateCompAccountTrieBranchOccupancy_5_Children  = dist.BranchOccupancyDistribution[4];  break;
                case 6:  StateCompAccountTrieBranchOccupancy_6_Children  = dist.BranchOccupancyDistribution[5];  break;
                case 7:  StateCompAccountTrieBranchOccupancy_7_Children  = dist.BranchOccupancyDistribution[6];  break;
                case 8:  StateCompAccountTrieBranchOccupancy_8_Children  = dist.BranchOccupancyDistribution[7];  break;
                case 9:  StateCompAccountTrieBranchOccupancy_9_Children  = dist.BranchOccupancyDistribution[8];  break;
                case 10: StateCompAccountTrieBranchOccupancy_10_Children = dist.BranchOccupancyDistribution[9];  break;
                case 11: StateCompAccountTrieBranchOccupancy_11_Children = dist.BranchOccupancyDistribution[10]; break;
                case 12: StateCompAccountTrieBranchOccupancy_12_Children = dist.BranchOccupancyDistribution[11]; break;
                case 13: StateCompAccountTrieBranchOccupancy_13_Children = dist.BranchOccupancyDistribution[12]; break;
                case 14: StateCompAccountTrieBranchOccupancy_14_Children = dist.BranchOccupancyDistribution[13]; break;
                case 15: StateCompAccountTrieBranchOccupancy_15_Children = dist.BranchOccupancyDistribution[14]; break;
                case 16: StateCompAccountTrieBranchOccupancy_16_Children = dist.BranchOccupancyDistribution[15]; break;
            }
        }

        StateCompAvgAccountPathDepth = dist.AvgAccountPathDepth;
        StateCompAvgStoragePathDepth = dist.AvgStoragePathDepth;
        StateCompMaxAccountDepth     = dist.MaxAccountDepth;
        StateCompMaxStorageDepth     = dist.MaxStorageDepth;
        StateCompAvgBranchOccupancy  = dist.AvgBranchOccupancy;
    }
}
