// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Evm.Tracing;

namespace Nethermind.Consensus;

public interface IBlockProducer
{
    Task<Block?> BuildBlock(
        BlockHeader? parentHeader = null,
        IBlockTracer? blockTracer = null,
        PayloadAttributes? payloadAttributes = null,
        CancellationToken cancellationToken = default);
}
