// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;

namespace Nethermind.Xdc;

/// <summary>
/// Enables the reward-tracing path in <see cref="Nethermind.Consensus.Processing.BlockProcessor"/>
/// while delegating all tracer callbacks to the outer tracer.
/// </summary>
internal sealed class RewardTracingBlockTracer(IBlockTracer inner) : IBlockTracer
{
    public bool IsTracingRewards => true;

    public void ReportReward(Address author, string rewardType, UInt256 rewardValue) =>
        inner.ReportReward(author, rewardType, rewardValue);

    public void StartNewBlockTrace(Block block) => inner.StartNewBlockTrace(block);

    public ITxTracer StartNewTxTrace(Transaction? tx) => inner.StartNewTxTrace(tx);

    public void EndTxTrace() => inner.EndTxTrace();

    public void EndBlockTrace() => inner.EndBlockTrace();
}
