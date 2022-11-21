// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Evm.Tracing
{
    /// <summary>
    /// Tracer for blocks.
    /// </summary>
    /// <remarks>
    /// This tracer should be reusable between blocks. <see cref="StartNewBlockTrace"/> call should reset inner tracer state.
    /// </remarks>
    public interface IBlockTracer
    {
        bool IsTracingRewards { get; }

        void ReportReward(Address author, string rewardType, UInt256 rewardValue);

        void StartNewBlockTrace(Block block);

        ITxTracer StartNewTxTrace(Transaction? tx);

        void EndTxTrace();

        void EndBlockTrace();
    }
}
