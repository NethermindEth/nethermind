//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
//
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
//
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
//
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

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
}
