// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm.State;
using Nethermind.Logging;

namespace Nethermind.Facade.Simulate;

/// <summary>
/// <see cref="BlockhashProvider"/> variant for eth_simulateV1 that treats an unresolvable in-window hash as 0
/// instead of throwing.
/// </summary>
/// <remarks>
/// Simulation runs over an overlay block tree whose ancestors may not be present, so a cache miss is expected
/// rather than an invariant violation. Returning null lets the EVM push 0 per BLOCKHASH semantics and keeps the
/// request best-effort. Canonical processing keeps the throwing guard from the base type.
/// </remarks>
public sealed class SimulateLenientBlockhashProvider(IBlockhashCache blockhashCache, IWorldState worldState, ILogManager logManager)
    : BlockhashProvider(blockhashCache, worldState, logManager)
{
    protected override Hash256? OnUnresolvedBlockhash(BlockHeader currentBlock, long number) => null;
}
