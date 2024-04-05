// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
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
        /// <summary>
        /// Is reward state change traced
        /// </summary>
        /// <remarks>
        /// Controls
        /// - <see cref="ReportReward"/>
        /// </remarks>
        bool IsTracingRewards { get; }

        /// <summary>
        /// Reports rewards for bock.
        /// </summary>
        /// <param name="author">Author/coinbase for reward.</param>
        /// <param name="rewardType">Type of reward.</param>
        /// <param name="rewardValue">Value of reward.</param>
        /// <remarks>Depends on <see cref="IsTracingRewards"/></remarks>
        void ReportReward(Address author, string rewardType, UInt256 rewardValue);

        /// <summary>
        /// Starts a trace for new block.
        /// </summary>
        /// <param name="block">Block to be traced.</param>
        void StartNewBlockTrace(Block block);

        /// <summary>
        /// Starts new transaction trace in a block.
        /// </summary>
        /// <param name="tx">Transaction this trace is started for. Null if it's reward trace.</param>
        /// <returns>Returns tracer for transaction.</returns>
        ITxTracer StartNewTxTrace(Transaction? tx);

        /// <summary>
        /// Ends last transaction trace <see cref="StartNewTxTrace"/>.
        /// </summary>
        void EndTxTrace();

        /// <summary>
        /// Ends block trace <see cref="StartNewBlockTrace"/>.
        /// </summary>
        void EndBlockTrace();
    }

    public interface IBlockTracer<out TTrace> : IBlockTracer
    {
        IReadOnlyCollection<TTrace> BuildResult();
    }
}
