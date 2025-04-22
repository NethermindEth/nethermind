// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Evm.Tracing;

namespace Nethermind.Consensus.Producers;

/// <summary>
/// A BlockProducer that always fails.
/// It is used in tests or when the node is not supposed to produce blocks, and we want to detect block production being triggered.
/// </summary>
public class FailBlockProducer : IBlockProducer
{
    public Task<Block?> BuildBlock(
        BlockHeader? parentHeader = null,
        IBlockTracer? blockTracer = null,
        PayloadAttributes? payloadAttributes = null,
        CancellationToken cancellationToken = default)
    {
        throw new InvalidOperationException("FailBlockProducer is not supposed to produce blocks.");
    }
}
