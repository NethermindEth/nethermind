// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Merge.Plugin.Synchronization;

/// <summary>Why a finalized block that could be caught up from its block access list was executed instead.</summary>
public enum BalCatchUpFallbackReason
{
    /// <summary>No list matching the header's commitment was attached.</summary>
    NoBlockAccessList,

    /// <summary>The list holds more items than the block's gas limit allows.</summary>
    ItemLimitExceeded,

    /// <summary>The block has transactions but its receipts are not stored.</summary>
    NoReceipts,

    /// <summary>Applying the list did not reproduce the header's state root.</summary>
    StateRootMismatch,
}
