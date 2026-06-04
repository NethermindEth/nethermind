// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Evm.Tracing;

namespace Nethermind.Consensus.Producers
{
    // TODO: seems to have quite a lot ofr an args class
    public class BlockProductionEventArgs(
        BlockHeader? parentHeader = null,
        CancellationToken? cancellationToken = null,
        IBlockTracer? blockTracer = null,
        PayloadAttributes? payloadAttributes = null) : EventArgs
    {
        public BlockHeader? ParentHeader { get; } = parentHeader;
        public IBlockTracer? BlockTracer { get; } = blockTracer;

        public PayloadAttributes? PayloadAttributes { get; } = payloadAttributes;
        public CancellationToken CancellationToken { get; } = cancellationToken ?? CancellationToken.None;
        public Task<Block?> BlockProductionTask { get; set; } = Task.FromResult<Block?>(null);

        public BlockProductionEventArgs Clone() => (BlockProductionEventArgs)MemberwiseClone();
    }
}
