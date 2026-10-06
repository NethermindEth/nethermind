// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.BeaconChain.Sync;

/// <summary>The log banner for the canonical head first reaching a Gloas slot.</summary>
internal static class GloasForkBanner
{
    internal const int MaxLines = 25;

    private const string PolarBear = """"""
                                         ___________
                                        |           |
                                        |   GLOAS   |
                                        |___________|
               __           __          |
              /  \.-"""""-./  \         |
              \   \       /   /         |
               |   o     o   |          |
               \   .-"""-.   /          |
                '-.\__Y__/.-'           |
                .-'`-----'`-.____      _|_
               /  /         \    `----(___)
              |  |           |
              |  |           |
              |  |           |
               \  \         /
              __\  \_______/__
             (____/       \____)
          ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~
        """""";

    /// <summary>Renders the banner for the head at <paramref name="slot"/> in <paramref name="epoch"/>.</summary>
    internal static string Render(ulong slot, ulong epoch) =>
        $"Beacon chain head crossed into Gloas at slot {slot} (epoch {epoch})\n{PolarBear}";
}
