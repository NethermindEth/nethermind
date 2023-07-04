// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;

namespace Nethermind.Consensus.Producers
{
    public interface IManualBlockProductionTrigger : IBlockProductionTrigger
    {
        // TODO: too many parameters
        public Task<Block?> BuildBlock(
            BlockHeader? parentHeader = null,
            CancellationToken? cancellationToken = null,
            IBlockTracer? blockTracer = null,
            PayloadAttributes? payloadAttributes = null);
    }
}
