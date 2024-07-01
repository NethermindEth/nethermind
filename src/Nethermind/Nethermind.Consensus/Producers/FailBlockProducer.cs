using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Evm.Tracing;

namespace Nethermind.Consensus.Producers;

public class FailBlockProducer : IBlockProducer
{
    public Task<Block?> BuildBlock(
        BlockHeader? parentHeader = null,
        IBlockTracer? blockTracer = null,
        PayloadAttributes? payloadAttributes = null,
        CancellationToken? cancellationToken = null)
    {
        throw new InvalidOperationException("FailBlockProducer is not supposed to produce blocks.");
    }
}
