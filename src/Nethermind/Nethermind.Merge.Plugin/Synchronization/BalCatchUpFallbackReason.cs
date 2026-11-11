// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Merge.Plugin.Synchronization;

/// <summary>Why a block being caught up was executed instead of being caught up from its block access list.</summary>
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

    /// <summary>A finalized block arrived through the engine API, which always executes: <c>engine_newPayload</c>,
    /// or a <c>forkchoiceUpdated</c> that queues an unprocessed head again.</summary>
    /// <remarks>Counted only for blocks older than two epochs, so following the chain head does not add to it.</remarks>
    EngineApi,

    /// <summary>The block is more than two epochs old, so it is being caught up, but the consensus client has not
    /// reported it finalized yet.</summary>
    /// <remarks>
    /// A consensus client that range-syncs after an outage hands over each block about two epochs before it
    /// finalizes it, so every such block lands here.
    /// </remarks>
    NotFinalized,
}
