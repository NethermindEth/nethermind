﻿//  Copyright (c) 2021 Demerzel Solutions Limited
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

namespace Nethermind.Blockchain.Producers
{
    public class BlockProductionEventArgs : EventArgs
    {
        public static readonly Task<Block?> DefaultBlockProductionTask = Task.FromResult<Block?>(null);
        public BlockHeader? ParentHeader { get; }
        public IBlockTracer? BlockTracer { get; }
        public CancellationToken CancellationToken { get; }
        public Task<Block?> BlockProductionTask { get; set; } = DefaultBlockProductionTask;

        public BlockProductionEventArgs(
            BlockHeader? parentHeader = null, 
            CancellationToken? cancellationToken = null,
            IBlockTracer? blockTracer = null)
        {
            ParentHeader = parentHeader;
            BlockTracer = blockTracer;
            CancellationToken = cancellationToken ?? CancellationToken.None;
        }
    }
}
