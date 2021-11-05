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
// 

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
