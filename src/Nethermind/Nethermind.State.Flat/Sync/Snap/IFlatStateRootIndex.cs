// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.State.Flat.Sync.Snap;

/// <summary>
/// Tracks mapping from state root hash to StateId for serving snap sync requests.
/// Similar to <see cref="ILastNStateRootTracker"/> but provides StateId for lookup.
/// </summary>
public interface IFlatStateRootIndex : ILastNStateRootTracker
{
    /// <summary>
    /// Try to get the StateId for a given state root hash.
    /// </summary>
    bool TryGetStateId(Hash256 stateRoot, out StateId stateId);
}
