//  Copyright (c) 2018 Demerzel Solutions Limited
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

using System;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Evm.Tracing;

namespace Nethermind.Blockchain.Synchronization.BeamSync
{
    public class BeamSyncBlockchainProcessor : IBlockchainProcessor
    {
        public void Start()
        {
        }

        public Task StopAsync(bool processRemainingBlocks = false)
        {
            return Task.CompletedTask;
        }

        public Block Process(Block block, ProcessingOptions options, IBlockTracer listener)
        {
            // process the block on the one time chain processor
            // if task is timing out shelve it and try next/
            // listen to incoming blocks with higher difficulty so that Tasks can be cancelled
            // ensure not leaving corrupted state
            // wrap the standard processor that will process actual blocks normally (when all the witness is collected
            
            // use prefetcher in the tx pool
            // use the same prefetcher here potentially?
            // prefetch code!
            throw new NotImplementedException();
        }
    }
}