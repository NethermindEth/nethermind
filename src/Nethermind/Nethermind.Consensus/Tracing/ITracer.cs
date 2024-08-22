// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm;
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
        /// Allows to trace an arbitrarily constructed block. Do NOT subtract gas from sender account
        /// </summary>
        /// <param name="block">Block to trace.</param>
        /// <param name="tracer">Trace to act on block processing events.</param>
        /// <param name="stateOverride">Optional state overrides by address.</param>
        void Trace(Block block, IBlockTracer tracer, Dictionary<Address, AccountOverride>? stateOverride = null);

        /// <summary>
        /// Allows to trace and verify arbitrary constructed block. Subtracts gas from sender account
        /// </summary>
        /// <param name="block">Block to trace.</param>
        /// <param name="tracer">Trace to act on block processing events.</param>
        void Execute(Block block, IBlockTracer tracer);

        void Accept(ITreeVisitor visitor, Hash256 stateRoot);
    }
}
