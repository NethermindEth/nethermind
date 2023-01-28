// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
        public ExecutionStatusHandler(
            IBlockFinder blockFinder)
        {
            _blockFinder = blockFinder;
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
