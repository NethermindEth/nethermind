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
using Nethermind.Blockchain;
using Nethermind.Blockchain.Producers;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Data;

namespace Nethermind.Merge.Plugin.Handlers
{
    public class AssembleBlockHandler : IHandlerAsync<AssembleBlockRequest, BlockRequestResult?>
    {
        private readonly IBlockTree _blockTree;
        private readonly IManualBlockProductionTrigger _blockProductionTrigger;
        private readonly ManualTimestamper _timestamper;
        private readonly ILogger _logger;
        private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(10);

        public AssembleBlockHandler(IBlockTree blockTree, IManualBlockProductionTrigger blockProductionTrigger, ManualTimestamper timestamper, ILogManager logManager)
        {
            _blockTree = blockTree;
            _blockProductionTrigger = blockProductionTrigger;
            _timestamper = timestamper;
            _logger = logManager.GetClassLogger();
        }

        public async Task<ResultWrapper<BlockRequestResult?>> HandleAsync(AssembleBlockRequest request)
        {
            BlockHeader? parentHeader = _blockTree.FindHeader(request.ParentHash);
            if (parentHeader is null)
            {
                if (_logger.IsWarn) _logger.Warn($"Parent block {request.ParentHash} cannot be found. New block will not be produced.");
                return ResultWrapper<BlockRequestResult?>.Success(null);
            }

            _timestamper.Set(DateTimeOffset.FromUnixTimeSeconds((long) request.Timestamp).UtcDateTime);
            using CancellationTokenSource cts = new(_timeout);
            Block? block = await _blockProductionTrigger.BuildBlock(parentHeader, cts.Token);
            if (block == null)
            {
                if (_logger.IsWarn) _logger.Warn($"Block production on parent {request.ParentHash} with timestamp {request.Timestamp} failed.");
                return ResultWrapper<BlockRequestResult?>.Success(null);
            }
            else
            {
                return ResultWrapper<BlockRequestResult?>.Success(new BlockRequestResult(block));
            }
        }
    }
}
