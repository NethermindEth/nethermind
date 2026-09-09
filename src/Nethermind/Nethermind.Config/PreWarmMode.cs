// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Config;

/// <summary>State pre-warming level applied while processing blocks.</summary>
public enum PreWarmMode
{
    /// <summary>No pre-warming.</summary>
    None,

    /// <summary>Warm the caches from the processed block's own transactions.</summary>
    Block,

    /// <summary>Also speculatively warm from the mempool in the gap between blocks.</summary>
    BlockAndMempool,
}
