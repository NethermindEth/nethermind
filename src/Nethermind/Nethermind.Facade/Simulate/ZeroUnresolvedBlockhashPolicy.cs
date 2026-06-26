// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Facade.Simulate;

/// <summary>
/// eth_simulateV1 policy: an unresolvable ancestor hash yields 0 (returns <c>null</c> so the EVM pushes 0)
/// rather than failing the whole request.
/// </summary>
/// <remarks>
/// Simulation runs over an overlay block tree whose ancestors may not be present, so a cache miss is expected
/// rather than an invariant violation. Injected into the simulate scope in place of the canonical
/// <see cref="ThrowingUnresolvedBlockhashPolicy"/>.
/// </remarks>
public sealed class ZeroUnresolvedBlockhashPolicy : IUnresolvedBlockhashPolicy
{
    public Hash256? Resolve(BlockHeader currentBlock, ulong number) => null;
}
