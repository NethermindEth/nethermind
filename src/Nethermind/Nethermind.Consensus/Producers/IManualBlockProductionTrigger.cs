// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Evm.Tracing;

namespace Nethermind.Consensus.Producers
{
    public interface IManualBlockProductionTrigger : IBlockProductionTrigger
    {
        /// <summary>
        /// Triggers building a block using the provided <see cref="BlockProductionEventArgs"/>.
        /// Prefer this overload to avoid multiple optional parameters.
        /// </summary>
        public Task<Block?> BuildBlock(BlockProductionEventArgs args);

        /// <summary>
        /// Triggers building a block. Prefer using <see cref="BuildBlock(BlockProductionEventArgs)"/>.
        /// </summary>
        [Obsolete("Use BuildBlock(BlockProductionEventArgs args) to reduce parameter count and improve readability.")]
        public Task<Block?> BuildBlock(
            BlockHeader? parentHeader = null,
            CancellationToken? cancellationToken = null,
            IBlockTracer? blockTracer = null,
            PayloadAttributes? payloadAttributes = null);
    }
}
