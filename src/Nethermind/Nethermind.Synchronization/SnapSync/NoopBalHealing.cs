// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Synchronization.SnapSync;

/// <summary>Used where BAL healing is unavailable; every heal attempt fails.</summary>
public sealed class NoopBalHealing : IBalHealing
{
    public static readonly NoopBalHealing Instance = new();
    private NoopBalHealing() { }

    public bool IsAvailable => false;

    public Hash256? Reassemble(IReadOnlyCollection<Hash256> updatedStorages, CancellationToken token) => null;

    public (bool BaseRootIntact, Hash256? Root) ApplyRange(Hash256 baseRoot, BlockHeader from, BlockHeader to, CancellationToken token) => (false, null);

    public Task<bool> TryRecoverMissingCode(CancellationToken token) => Task.FromResult(true);

    public void FinalizeSync(BlockHeader pivot) { }
}
