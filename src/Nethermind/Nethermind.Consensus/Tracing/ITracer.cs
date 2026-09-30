// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Evm.Tracing;
using Nethermind.Trie;

namespace Nethermind.Consensus.Tracing
{
    /// <summary>
    /// A simple and flexible bridge for any tracing operations on blocks and transactions.
    /// </summary>
    public interface ITracer
    {
        /// <summary>
        /// Allows to trace an arbitrarily constructed block. A transaction with both fee caps zero pays no gas fee; any other is validated and charged for gas.
        /// </summary>
        /// <param name="block">Block to trace.</param>
        /// <param name="tracer">Trace to act on block processing events.</param>
        void Trace(Block block, IBlockTracer tracer);

        /// <summary>
        /// Allows to trace and verify arbitrary constructed block. Subtracts gas from sender account
        /// </summary>
        /// <param name="block">Block to trace.</param>
        /// <param name="tracer">Trace to act on block processing events.</param>
        void Execute(Block block, IBlockTracer tracer);

        /// <summary>
        /// Allows to trace an arbitrarily constructed block of signed transactions, each run with its own nonce, which the
        /// processor validates with its other state checks, such as the sender's code and funds; fees are charged. Unlike
        /// <see cref="Execute"/>, the nonce isn't replaced with the sender's. The caller validates that each is well formed.
        /// </summary>
        /// <param name="block">Block to trace.</param>
        /// <param name="tracer">Trace to act on block processing events.</param>
        void ExecuteSigned(Block block, IBlockTracer tracer);

        void Accept<TCtx>(ITreeVisitor<TCtx> visitor, BlockHeader? baseBlock) where TCtx : struct, INodeContext<TCtx>;
    }
}
