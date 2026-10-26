// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;

namespace Nethermind.BeaconChain.StateTransition.Hashing;

/// <summary>Stateless <see cref="IBeaconStateHasher"/> that re-merkleizes the whole state on every call.</summary>
public sealed class FullBeaconStateHasher : IBeaconStateHasher
{
    /// <inheritdoc/>
    public Hash256 HashTreeRoot(BeaconStateFulu state) => SszRoots.HashTreeRoot(state);
}
