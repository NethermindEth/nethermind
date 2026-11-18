// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.Contracts;

namespace Nethermind.Consensus.Rewards;

/// <summary>
/// The gas limit of the AuRa reward contract call. When none is registered, as in block production and processing,
/// the call is not limited: it uses <see cref="CallableContract.UnlimitedGas"/>.
/// </summary>
/// <param name="Value">The limit, in gas units.</param>
public sealed record RewardContractGasLimit(ulong Value)
{
    /// <summary>
    /// The limit for blocks that RPC requests trace or simulate: <see cref="CallableContract.SystemTransactionGasLimit"/>,
    /// 30,000,000 gas.
    /// </summary>
    public static readonly RewardContractGasLimit Rpc = new(CallableContract.SystemTransactionGasLimit);
}
