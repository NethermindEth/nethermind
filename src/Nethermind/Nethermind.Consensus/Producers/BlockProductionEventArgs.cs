// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;

namespace Nethermind.Consensus.Producers
{
    // TODO: seems to have quite a lot ofr an args class
    public class BlockProductionEventArgs : EventArgs
    {
        public BlockHeader? ParentHeader { get; }
        public IBlockTracer? BlockTracer { get; }

        public PayloadAttributes? PayloadAttributes { get; }
        public CancellationToken CancellationToken { get; }
        public Task<Block?> BlockProductionTask { get; set; } = Task.FromResult<Block?>(null);

        public BlockProductionEventArgs(
            BlockHeader? parentHeader = null,
            CancellationToken? cancellationToken = null,
            IBlockTracer? blockTracer = null,
            PayloadAttributes? payloadAttributes = null)
        {
            ParentHeader = parentHeader;
            BlockTracer = blockTracer;
            PayloadAttributes = payloadAttributes;
            CancellationToken = cancellationToken ?? CancellationToken.None;
        }

        public BlockProductionEventArgs Clone() => (BlockProductionEventArgs)MemberwiseClone();
    }
}
