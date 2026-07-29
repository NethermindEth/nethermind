// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Facade.Simulate;

/// <summary>
/// eth_simulateV1 best-effort policy: an unresolvable ancestor hash returns <c>null</c> (the EVM pushes 0) instead of failing the request.
/// </summary>
public sealed class NullUnresolvedBlockhashPolicy : IUnresolvedBlockhashPolicy
{
    public Hash256? Resolve(BlockHeader currentBlock, ulong number) => null;
}
