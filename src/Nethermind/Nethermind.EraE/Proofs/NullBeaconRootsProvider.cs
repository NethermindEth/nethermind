// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.EraE.Proofs;

public sealed class NullBeaconRootsProvider : IBeaconRootsProvider
{
    public static readonly NullBeaconRootsProvider Instance = new();

    private NullBeaconRootsProvider() { }

    public Task<(ValueHash256 BeaconBlockRoot, ValueHash256 StateRoot)?> GetBeaconRoots(
        long slot, CancellationToken cancellationToken = default) =>
        Task.FromResult<(ValueHash256, ValueHash256)?>(null);

    public void Dispose() { }
}
