// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Data.V1;

namespace Nethermind.Merge.Plugin.Handlers.V1
{
    public class GetPayloadBodiesByHashV1Handler : IAsyncHandler<Keccak[], ExecutionPayloadBodyV1Result?[]>
    {
        private readonly IBlockTree _blockTree;
        private readonly ILogger _logger;

        public GetPayloadBodiesByHashV1Handler(IBlockTree blockTree, ILogManager logManager)
        {
            _blockTree = blockTree;
            _logger = logManager.GetClassLogger();
        }

        public Task<ResultWrapper<ExecutionPayloadBodyV1Result?[]>> HandleAsync(Keccak[] blockHashes)
        {
            var payloadBodies = new ExecutionPayloadBodyV1Result?[blockHashes.Length];
            for (int i = 0; i < blockHashes.Length; ++i)
            {
                Block? block = _blockTree.FindBlock(blockHashes[i]);

                payloadBodies[i] = block is not null ? new ExecutionPayloadBodyV1Result(block.Transactions) : null;
            }

            return Task.FromResult(ResultWrapper<ExecutionPayloadBodyV1Result?[]>.Success(payloadBodies));
        }
    }
}
