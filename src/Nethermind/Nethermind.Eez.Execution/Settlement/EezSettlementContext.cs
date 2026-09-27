// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Eez.Execution.Settlement;

/// <summary>The deployment an attester signs for: the rollup, its L2 chain and the proof system that verifies it.</summary>
/// <param name="BlockTimeSeconds">The L2 block cadence derivation adds to each parent's timestamp.</param>
/// <param name="GasLimit">The gas limit derivation gives every L2 block.</param>
public sealed record EezSettlementContext(ulong RollupId, ulong ChainId, Address ProofSystem, ValueHash256 VerificationKey, ulong BlockTimeSeconds,
    ulong GasLimit = EezSettlementContext.DefaultGasLimit)
{
    public const ulong DefaultGasLimit = 30_000_000;
}
