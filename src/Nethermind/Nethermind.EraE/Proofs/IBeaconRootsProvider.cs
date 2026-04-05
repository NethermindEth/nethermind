// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.EraE.Proofs;

/// <summary>
/// Provides beacon block roots and state roots by beacon slot.
/// Used during EraE export to generate post-merge block proofs.
/// </summary>
public interface IBeaconRootsProvider : IDisposable
{
    /// <summary>
    /// Returns the beacon block root and state root for the given beacon slot,
    /// or null if the data is unavailable.
    /// </summary>
    Task<(ValueHash256 BeaconBlockRoot, ValueHash256 StateRoot)?> GetBeaconRoots(
        long slot, CancellationToken cancellationToken = default);
}
