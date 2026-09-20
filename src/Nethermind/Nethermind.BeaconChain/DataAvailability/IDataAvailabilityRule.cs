// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;

namespace Nethermind.BeaconChain.DataAvailability;

/// <summary>
/// The fork-choice caller's chosen reading of the spec's <c>is_data_available</c>: which column
/// sidecars a block needs before <c>on_block</c> may accept it.
/// </summary>
/// <remarks>
/// Two rules exist and a caller must pick one deliberately, because they answer different questions
/// and neither can stand in for the other. <see cref="FullColumnSetAvailability"/> is the supernode
/// view the consensus-spec fork choice vectors are written against: every one of the 128 columns is
/// handed over and every one must verify. <see cref="CustodySamplingAvailability"/> is what a real
/// node can actually satisfy: its own custody columns and its per-slot sample must be held and
/// verified. Applying the first to a live node rejects every blob block (a base-custody node never
/// holds 128 columns); applying the second to the vectors would silently pass blocks the vectors
/// expect rejected.
/// </remarks>
public interface IDataAvailabilityRule
{
    /// <summary>
    /// Whether every piece of blob data this rule demands for <paramref name="block"/> is present and
    /// verified. A block with no blob commitments needs no columns and is trivially available.
    /// </summary>
    /// <param name="blockRoot"><c>hash_tree_root(block)</c>, which every sidecar's header must round-trip to.</param>
    bool IsDataAvailable(BeaconBlock block, Hash256 blockRoot, BeaconChainSpec spec);
}
