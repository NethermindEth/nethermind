// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.StateComposition.Test;

/// <summary>
/// Explicit, non-reflection reset of every per-depth and branch-occupancy gauge on
/// <see cref="Metrics"/>. Kept explicit so the reset set is obvious at review time
/// and the compiler catches gauge renames.
/// </summary>
internal static class MetricsDepthGaugesHelper
{
    public static void ResetAllDepthGauges()
    {
        Metrics.StateCompAvgAccountPathDepth = 0;
        Metrics.StateCompAvgStoragePathDepth = 0;
        Metrics.StateCompMaxAccountDepth = 0;
        Metrics.StateCompMaxStorageDepth = 0;
        Metrics.StateCompAvgBranchOccupancy = 0;

        for (int d = 0; d < 16; d++) ResetDepth(d);

        Metrics.StateCompAccountTrieBranchOccupancy_1_Children = 0;
        Metrics.StateCompAccountTrieBranchOccupancy_2_Children = 0;
        Metrics.StateCompAccountTrieBranchOccupancy_3_Children = 0;
        Metrics.StateCompAccountTrieBranchOccupancy_4_Children = 0;
        Metrics.StateCompAccountTrieBranchOccupancy_5_Children = 0;
        Metrics.StateCompAccountTrieBranchOccupancy_6_Children = 0;
        Metrics.StateCompAccountTrieBranchOccupancy_7_Children = 0;
        Metrics.StateCompAccountTrieBranchOccupancy_8_Children = 0;
        Metrics.StateCompAccountTrieBranchOccupancy_9_Children = 0;
        Metrics.StateCompAccountTrieBranchOccupancy_10_Children = 0;
        Metrics.StateCompAccountTrieBranchOccupancy_11_Children = 0;
        Metrics.StateCompAccountTrieBranchOccupancy_12_Children = 0;
        Metrics.StateCompAccountTrieBranchOccupancy_13_Children = 0;
        Metrics.StateCompAccountTrieBranchOccupancy_14_Children = 0;
        Metrics.StateCompAccountTrieBranchOccupancy_15_Children = 0;
        Metrics.StateCompAccountTrieBranchOccupancy_16_Children = 0;
    }

    private static void ResetDepth(int d)
    {
        switch (d)
        {
            case 0: Metrics.StateCompAccountTrieDepth_0_FullNodes = Metrics.StateCompAccountTrieDepth_0_ShortNodes = Metrics.StateCompAccountTrieDepth_0_ValueNodes = Metrics.StateCompAccountTrieDepth_0_Bytes = 0; Metrics.StateCompStorageTrieDepth_0_FullNodes = Metrics.StateCompStorageTrieDepth_0_ShortNodes = Metrics.StateCompStorageTrieDepth_0_ValueNodes = Metrics.StateCompStorageTrieDepth_0_Bytes = 0; break;
            case 1: Metrics.StateCompAccountTrieDepth_1_FullNodes = Metrics.StateCompAccountTrieDepth_1_ShortNodes = Metrics.StateCompAccountTrieDepth_1_ValueNodes = Metrics.StateCompAccountTrieDepth_1_Bytes = 0; Metrics.StateCompStorageTrieDepth_1_FullNodes = Metrics.StateCompStorageTrieDepth_1_ShortNodes = Metrics.StateCompStorageTrieDepth_1_ValueNodes = Metrics.StateCompStorageTrieDepth_1_Bytes = 0; break;
            case 2: Metrics.StateCompAccountTrieDepth_2_FullNodes = Metrics.StateCompAccountTrieDepth_2_ShortNodes = Metrics.StateCompAccountTrieDepth_2_ValueNodes = Metrics.StateCompAccountTrieDepth_2_Bytes = 0; Metrics.StateCompStorageTrieDepth_2_FullNodes = Metrics.StateCompStorageTrieDepth_2_ShortNodes = Metrics.StateCompStorageTrieDepth_2_ValueNodes = Metrics.StateCompStorageTrieDepth_2_Bytes = 0; break;
            case 3: Metrics.StateCompAccountTrieDepth_3_FullNodes = Metrics.StateCompAccountTrieDepth_3_ShortNodes = Metrics.StateCompAccountTrieDepth_3_ValueNodes = Metrics.StateCompAccountTrieDepth_3_Bytes = 0; Metrics.StateCompStorageTrieDepth_3_FullNodes = Metrics.StateCompStorageTrieDepth_3_ShortNodes = Metrics.StateCompStorageTrieDepth_3_ValueNodes = Metrics.StateCompStorageTrieDepth_3_Bytes = 0; break;
            case 4: Metrics.StateCompAccountTrieDepth_4_FullNodes = Metrics.StateCompAccountTrieDepth_4_ShortNodes = Metrics.StateCompAccountTrieDepth_4_ValueNodes = Metrics.StateCompAccountTrieDepth_4_Bytes = 0; Metrics.StateCompStorageTrieDepth_4_FullNodes = Metrics.StateCompStorageTrieDepth_4_ShortNodes = Metrics.StateCompStorageTrieDepth_4_ValueNodes = Metrics.StateCompStorageTrieDepth_4_Bytes = 0; break;
            case 5: Metrics.StateCompAccountTrieDepth_5_FullNodes = Metrics.StateCompAccountTrieDepth_5_ShortNodes = Metrics.StateCompAccountTrieDepth_5_ValueNodes = Metrics.StateCompAccountTrieDepth_5_Bytes = 0; Metrics.StateCompStorageTrieDepth_5_FullNodes = Metrics.StateCompStorageTrieDepth_5_ShortNodes = Metrics.StateCompStorageTrieDepth_5_ValueNodes = Metrics.StateCompStorageTrieDepth_5_Bytes = 0; break;
            case 6: Metrics.StateCompAccountTrieDepth_6_FullNodes = Metrics.StateCompAccountTrieDepth_6_ShortNodes = Metrics.StateCompAccountTrieDepth_6_ValueNodes = Metrics.StateCompAccountTrieDepth_6_Bytes = 0; Metrics.StateCompStorageTrieDepth_6_FullNodes = Metrics.StateCompStorageTrieDepth_6_ShortNodes = Metrics.StateCompStorageTrieDepth_6_ValueNodes = Metrics.StateCompStorageTrieDepth_6_Bytes = 0; break;
            case 7: Metrics.StateCompAccountTrieDepth_7_FullNodes = Metrics.StateCompAccountTrieDepth_7_ShortNodes = Metrics.StateCompAccountTrieDepth_7_ValueNodes = Metrics.StateCompAccountTrieDepth_7_Bytes = 0; Metrics.StateCompStorageTrieDepth_7_FullNodes = Metrics.StateCompStorageTrieDepth_7_ShortNodes = Metrics.StateCompStorageTrieDepth_7_ValueNodes = Metrics.StateCompStorageTrieDepth_7_Bytes = 0; break;
            case 8: Metrics.StateCompAccountTrieDepth_8_FullNodes = Metrics.StateCompAccountTrieDepth_8_ShortNodes = Metrics.StateCompAccountTrieDepth_8_ValueNodes = Metrics.StateCompAccountTrieDepth_8_Bytes = 0; Metrics.StateCompStorageTrieDepth_8_FullNodes = Metrics.StateCompStorageTrieDepth_8_ShortNodes = Metrics.StateCompStorageTrieDepth_8_ValueNodes = Metrics.StateCompStorageTrieDepth_8_Bytes = 0; break;
            case 9: Metrics.StateCompAccountTrieDepth_9_FullNodes = Metrics.StateCompAccountTrieDepth_9_ShortNodes = Metrics.StateCompAccountTrieDepth_9_ValueNodes = Metrics.StateCompAccountTrieDepth_9_Bytes = 0; Metrics.StateCompStorageTrieDepth_9_FullNodes = Metrics.StateCompStorageTrieDepth_9_ShortNodes = Metrics.StateCompStorageTrieDepth_9_ValueNodes = Metrics.StateCompStorageTrieDepth_9_Bytes = 0; break;
            case 10: Metrics.StateCompAccountTrieDepth_10_FullNodes = Metrics.StateCompAccountTrieDepth_10_ShortNodes = Metrics.StateCompAccountTrieDepth_10_ValueNodes = Metrics.StateCompAccountTrieDepth_10_Bytes = 0; Metrics.StateCompStorageTrieDepth_10_FullNodes = Metrics.StateCompStorageTrieDepth_10_ShortNodes = Metrics.StateCompStorageTrieDepth_10_ValueNodes = Metrics.StateCompStorageTrieDepth_10_Bytes = 0; break;
            case 11: Metrics.StateCompAccountTrieDepth_11_FullNodes = Metrics.StateCompAccountTrieDepth_11_ShortNodes = Metrics.StateCompAccountTrieDepth_11_ValueNodes = Metrics.StateCompAccountTrieDepth_11_Bytes = 0; Metrics.StateCompStorageTrieDepth_11_FullNodes = Metrics.StateCompStorageTrieDepth_11_ShortNodes = Metrics.StateCompStorageTrieDepth_11_ValueNodes = Metrics.StateCompStorageTrieDepth_11_Bytes = 0; break;
            case 12: Metrics.StateCompAccountTrieDepth_12_FullNodes = Metrics.StateCompAccountTrieDepth_12_ShortNodes = Metrics.StateCompAccountTrieDepth_12_ValueNodes = Metrics.StateCompAccountTrieDepth_12_Bytes = 0; Metrics.StateCompStorageTrieDepth_12_FullNodes = Metrics.StateCompStorageTrieDepth_12_ShortNodes = Metrics.StateCompStorageTrieDepth_12_ValueNodes = Metrics.StateCompStorageTrieDepth_12_Bytes = 0; break;
            case 13: Metrics.StateCompAccountTrieDepth_13_FullNodes = Metrics.StateCompAccountTrieDepth_13_ShortNodes = Metrics.StateCompAccountTrieDepth_13_ValueNodes = Metrics.StateCompAccountTrieDepth_13_Bytes = 0; Metrics.StateCompStorageTrieDepth_13_FullNodes = Metrics.StateCompStorageTrieDepth_13_ShortNodes = Metrics.StateCompStorageTrieDepth_13_ValueNodes = Metrics.StateCompStorageTrieDepth_13_Bytes = 0; break;
            case 14: Metrics.StateCompAccountTrieDepth_14_FullNodes = Metrics.StateCompAccountTrieDepth_14_ShortNodes = Metrics.StateCompAccountTrieDepth_14_ValueNodes = Metrics.StateCompAccountTrieDepth_14_Bytes = 0; Metrics.StateCompStorageTrieDepth_14_FullNodes = Metrics.StateCompStorageTrieDepth_14_ShortNodes = Metrics.StateCompStorageTrieDepth_14_ValueNodes = Metrics.StateCompStorageTrieDepth_14_Bytes = 0; break;
            case 15: Metrics.StateCompAccountTrieDepth_15_FullNodes = Metrics.StateCompAccountTrieDepth_15_ShortNodes = Metrics.StateCompAccountTrieDepth_15_ValueNodes = Metrics.StateCompAccountTrieDepth_15_Bytes = 0; Metrics.StateCompStorageTrieDepth_15_FullNodes = Metrics.StateCompStorageTrieDepth_15_ShortNodes = Metrics.StateCompStorageTrieDepth_15_ValueNodes = Metrics.StateCompStorageTrieDepth_15_Bytes = 0; break;
        }
    }
}
