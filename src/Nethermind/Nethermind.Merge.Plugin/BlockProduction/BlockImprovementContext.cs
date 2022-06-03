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
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Evm.Tracing;

namespace Nethermind.Merge.Plugin.BlockProduction;

public class BlockImprovementContext : IBlockImprovementContext
{
    private readonly CancellationTokenSource _cancellationTokenSource;
    
    public BlockImprovementContext(
        Block currentBestBlock, 
        IManualBlockProductionTrigger blockProductionTrigger, 
        TimeSpan timeout,
        BlockHeader parentHeader, 
        PayloadAttributes payloadAttributes)
    {
        _cancellationTokenSource = new CancellationTokenSource(timeout);
        CurrentBestBlock = currentBestBlock;
        ImprovementTask = blockProductionTrigger
            .BuildBlock(parentHeader, _cancellationTokenSource.Token, NullBlockTracer.Instance, payloadAttributes)
            .ContinueWith(SetCurrentBestBlock, _cancellationTokenSource.Token);
    }

    public Task<Block?> ImprovementTask { get; }

    public Block? CurrentBestBlock { get; private set; }

    private Block? SetCurrentBestBlock(Task<Block?> task)
    {
        if (task.IsCompletedSuccessfully)
        {
            if (task.Result != null)
            {
                CurrentBestBlock = task.Result;
            }
        }
                
        return task.Result;
    }

    public void Dispose()
    {
        _cancellationTokenSource.Cancel();
        _cancellationTokenSource.Dispose();
    }
}
