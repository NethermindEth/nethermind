// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Serialization;
using Nethermind.Optimism.CL;

namespace Nethermind.Optimism.Cl.Rpc;

/// <summary>
/// Represents a snapshot of the rollup driver.
/// Values may be zeroed if not yet initialized.
/// </summary>
/// <remarks>
/// Spec: https://specs.optimism.io/protocol/rollup-node.html?utm_source=op-docs&utm_medium=docs#syncstatus
/// </remarks>
public sealed record OptimismSyncStatus
{
    #region L1
    /// <summary>
    /// Is the L1 block that the derivation process is last idled at.
	/// This may not be fully derived into L2 data yet.
	/// The safe L2 blocks were produced/included fully from the L1 chain up to _but excluding_ this L1 block.
	/// If the node is synced, this matches the HeadL1, minus the verifier confirmation distance.
    /// </summary>
    [JsonPropertyName("current_l1")]
    public required L1BlockRef CurrentL1 { get; init; }

    /// <summary>
    /// CurrentL1Finalized is a legacy sync-status attribute. This is deprecated.
    /// A previous version of the L1 finalization-signal was updated only after the block was retrieved by number.
    /// This attribute just matches FinalizedL1 now.
    /// </summary>
    [JsonPropertyName("current_l1_finalized")]
    public L1BlockRef CurrentL1Finalized => FinalizedL1;

    /// <summary>
    /// HeadL1 is the perceived head of the L1 chain, no confirmation distance.
	/// The head is not guaranteed to build on the other L1 sync status fields,
	/// as the node may be in progress of resetting to adapt to a L1 reorg.
    /// </summary>
    [JsonPropertyName("head_l1")]
    public required L1BlockRef HeadL1 { get; init; }
    [JsonPropertyName("safe_l1")]
    public required L1BlockRef SafeL1 { get; init; }
    [JsonPropertyName("finalized_l1")]
    public required L1BlockRef FinalizedL1 { get; init; }
    #endregion

    #region L2
    /// <summary>
    /// The absolute tip of the L2 chain,
	/// pointing to block data that has not been submitted to L1 yet.
	/// The sequencer is building this, and verifiers may also be ahead of the
	/// SafeL2 block if they sync blocks via p2p or other offchain sources.
	/// This is considered to only be local-unsafe post-interop, see CrossUnsafe for cross-L2 guarantees.
    /// </summary>
    [JsonPropertyName("unsafe_l2")]
    public required L2BlockRef UnsafeL2 { get; init; }

    /// <summary>
    /// Points to the L2 block that was derived from the L1 chain.
	/// This point may still reorg if the L1 chain reorgs.
	/// This is considered to be cross-safe post-interop, see LocalSafe to ignore cross-L2 guarantees.
    /// </summary>
    [JsonPropertyName("safe_l2")]
    public required L2BlockRef SafeL2 { get; init; }

    /// <summary>
    /// Points to the L2 block that was derived fully from finalized L1 information, thus irreversible.
    /// </summary>
    [JsonPropertyName("finalized_l2")]
    public required L2BlockRef FinalizedL2 { get; init; }

    /// <summary>
    /// Points to the L2 block processed from the batch, but not consolidated to the safe block yet.
    /// </summary>
    [JsonPropertyName("pending_safe_l2")]
    public required L2BlockRef PendingSafeL2 { get; init; }
    [JsonPropertyName("queued_unsafe_l2")]
    public required L2BlockRef QueuedUnsafeL2 { get; init; }
    #endregion
}
