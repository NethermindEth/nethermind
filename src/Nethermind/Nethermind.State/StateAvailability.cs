// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State;

/// <summary>
/// Historical state availability of a world-state implementation, reported through
/// <c>eth_capabilities</c>. Three states are distinguishable:
/// <list type="bullet">
/// <item>Archive: <c>Archive=true</c>, <c>RetentionWindowBlocks=null</c> — state from genesis.</item>
/// <item>Rolling: <c>Archive=false</c>, <c>RetentionWindowBlocks=N</c> — last N blocks.</item>
/// <item>Unknown: <c>Archive=false</c>, <c>RetentionWindowBlocks=null</c> — non-linear retention (full pruning).</item>
/// </list>
/// </summary>
public readonly record struct StateAvailability(
    bool Archive,
    long? RetentionWindowBlocks);
