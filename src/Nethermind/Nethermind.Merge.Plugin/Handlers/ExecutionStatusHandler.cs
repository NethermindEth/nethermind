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
using Nethermind.JsonRpc;
using Nethermind.Merge.Plugin.Data;

namespace Nethermind.Merge.Plugin.Handlers
{
    /// <summary>
    /// The final spec doesn't include this endpoint to the final spec. However, it will be a useful diagnostic endpoint.
    /// </summary>
    public class ExecutionStatusHandler : IHandler<ExecutionStatusResult>
    {
        private readonly IBlockFinder _blockFinder;
        private readonly IBlockConfirmationManager _blockConfirmationManager;
        private readonly IManualBlockFinalizationManager _blockFinalizationManager;

        public ExecutionStatusHandler(
            IBlockFinder blockFinder,
            IBlockConfirmationManager blockConfirmationManager,
            IManualBlockFinalizationManager blockFinalizationManager)
        {
            _blockFinder = blockFinder;
            _blockConfirmationManager = blockConfirmationManager;
            _blockFinalizationManager = blockFinalizationManager;
        }

        public ResultWrapper<ExecutionStatusResult> Handle()
        {
            return ResultWrapper<ExecutionStatusResult>.Success(new ExecutionStatusResult(
                    _blockFinder.HeadHash,
                    _blockFinder.FinalizedHash!,
                    _blockFinder.SafeHash!)
            );
        }
    }
}
