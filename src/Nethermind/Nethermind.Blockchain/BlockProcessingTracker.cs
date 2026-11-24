// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Blockchain;

/// <summary>Whether the main processing loop is executing blocks.</summary>
public sealed class BlockProcessingTracker
{
    public bool IsProcessingBlock { get; set; }
}
