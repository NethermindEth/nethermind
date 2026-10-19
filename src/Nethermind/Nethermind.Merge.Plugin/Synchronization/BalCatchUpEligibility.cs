// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Merge.Plugin.Synchronization;

/// <summary>Whether a block can be caught up from its block access list, as decided by <see cref="FinalizedBlockAccessListPolicy"/>.</summary>
public enum BalCatchUpEligibility
{
    /// <summary>The block is on the consensus-finalized ancestry and above the processed head.</summary>
    Eligible,

    /// <summary>The feature, the chain or the block does not support reconstruction, for example a pre-EIP-7928
    /// block or a block with transactions on a node that does not store receipts.</summary>
    Unsupported,

    /// <summary>The block is at or below the processed head.</summary>
    AlreadyProcessed,

    /// <summary>The block is not on the ancestry of the finalized block the consensus client last reported.</summary>
    NotFinalized,
}
