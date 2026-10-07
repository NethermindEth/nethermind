// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;

namespace Nethermind.Consensus.Processing;

/// <summary>Whether the block the processing thread executes may take shifted replays, and whether it took any.</summary>
internal static class ShiftedReplay
{
    [ThreadStatic] private static bool t_allowed;
    [ThreadStatic] private static int t_used;

    public static bool Allowed { get => t_allowed; set => t_allowed = value; }

    /// <summary>Shifted replays the current block took.</summary>
    public static int Used { get => t_used; set => t_used = value; }
}

/// <summary>A block that took shifted replays did not end in its header's roots; it is processed again without them.</summary>
internal sealed class ShiftedReplayRetryException(Block block)
    : Exception($"Block {block.ToString(Block.Format.FullHashAndNumber)} did not end in its roots with shifted replays.");
