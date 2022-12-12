// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Data;

namespace Nethermind.Merge.Plugin.Handlers
{
    public class GetPayloadBodiesV1Handler : IAsyncHandler<Keccak[], ExecutionPayloadBodyV1Result[]>
    {
        private readonly IBlockTree _blockTree;
        private readonly ILogger _logger;

        public GetPayloadBodiesV1Handler(IBlockTree blockTree, ILogManager logManager)
        {
            _blockTree = blockTree;
            _logger = logManager.GetClassLogger();
        }

        public Task<ResultWrapper<ExecutionPayloadBodyV1Result[]>> HandleAsync(Keccak[] blockHashes)
        {
            List<ExecutionPayloadBodyV1Result> payloadBodies = new(blockHashes.Length);
            foreach (Keccak hash in blockHashes)
            {
                Block? block = _blockTree.FindBlock(hash);
                if (block is not null)
                {
                    payloadBodies.Add(new ExecutionPayloadBodyV1Result(block.Transactions));
                }
            }

            return Task.FromResult(ResultWrapper<ExecutionPayloadBodyV1Result[]>.Success(payloadBodies.ToArray()));
        }
    }
}
