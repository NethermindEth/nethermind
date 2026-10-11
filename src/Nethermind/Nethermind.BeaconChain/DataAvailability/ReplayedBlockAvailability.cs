// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;

namespace Nethermind.BeaconChain.DataAvailability;

/// <summary>
/// The <c>is_data_available</c> reading for a block this node already imported once: <c>on_block</c>
/// asserted its availability before the block was persisted, and the sidecars that satisfied it are
/// not persisted, so a replay from the store can neither re-check them nor needs to.
/// </summary>
/// <remarks>
/// Only valid for blocks that came out of this node's own store. Applying it to a block from the
/// network turns the gate off for that block, which is exactly the hole the other rules exist to
/// close, so the caller must select it by the same trusted-replay flag that skips signature checks.
/// </remarks>
public sealed class ReplayedBlockAvailability : IDataAvailabilityRule
{
    public static readonly ReplayedBlockAvailability Instance = new();

    private ReplayedBlockAvailability()
    {
    }

    /// <inheritdoc/>
    public bool IsDataAvailable(BeaconBlock block, Hash256 blockRoot, BeaconChainSpec spec) => true;
}
