// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Synchronization.SnapSync;

public sealed class NoopBalHealing : IBalHealing
{
    public static readonly NoopBalHealing Instance = new();
    private NoopBalHealing() { }

    public Hash256? Reassemble(IReadOnlyCollection<Hash256> updatedStorages) => null;

    public Hash256? ApplyRange(Hash256 baseRoot, BlockHeader from, BlockHeader to, CancellationToken token) => null;

    public void FinalizeSync(BlockHeader pivot) { }
}
