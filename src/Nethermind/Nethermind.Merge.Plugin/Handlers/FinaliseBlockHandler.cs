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

using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Result = Nethermind.Merge.Plugin.Data.Result;

namespace Nethermind.Merge.Plugin.Handlers
{
    public class FinaliseBlockHandler : IHandler<Keccak, Result>
    {
        private readonly IBlockFinder _blockFinder;
        private readonly IManualBlockFinalizationManager _manualBlockFinalizationManager;
        private readonly ILogger _logger;

        public FinaliseBlockHandler(IBlockFinder blockFinder, IManualBlockFinalizationManager manualBlockFinalizationManager, ILogManager logManager)
        {
            _blockFinder = blockFinder;
            _manualBlockFinalizationManager = manualBlockFinalizationManager;
            _logger = logManager.GetClassLogger();
        }

        public ResultWrapper<Result> Handle(Keccak request)
        {
            BlockHeader? blockHeader = _blockFinder.FindHeader(request, BlockTreeLookupOptions.None);
            if (blockHeader is null)
            {
                if (_logger.IsWarn) _logger.Warn($"Block {request} not found for finalization.");
                return ResultWrapper<Result>.Success(Result.Fail);
            }
            
            BlockHeader? headHeader = _blockFinder.Head?.Header;

            if (headHeader is null)
            {
                if (_logger.IsWarn) _logger.Warn($"Can't finalize block {request}. Head is null.");
                return ResultWrapper<Result>.Success(Result.Fail);                
            }
            
            _manualBlockFinalizationManager.MarkFinalized(headHeader, blockHeader);
            return ResultWrapper<Result>.Success(Result.Ok);
        }
    }
}
