// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Consensus.Tracing;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Db;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.JsonRpc.Data;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.Modules.DebugModule;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Synchronization.ParallelSync;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nethermind.Merge.Plugin
{
    public class MergeDebugBridge : DebugBridge, IMergeDebugBridge
    {
        private readonly IBlockProducer _blockProducer;

        public MergeDebugBridge(
            IConfigProvider configProvider,
            IReadOnlyDbProvider dbProvider,
            IGethStyleTracer tracer,
            IBlockTree blockTree,
            IReceiptStorage receiptStorage,
            IReceiptsMigration receiptsMigration,
            ISpecProvider specProvider,
            ISyncModeSelector syncModeSelector,
            IBadBlockStore badBlockStore,
            IBlockProducer blockProducer)
        : base(configProvider, dbProvider, tracer, blockTree, receiptStorage, receiptsMigration, specProvider, syncModeSelector, badBlockStore)
        {
            _blockProducer = blockProducer ?? throw new ArgumentNullException(nameof(blockProducer));
        }

        public ExecutionPayloadForRpc GenerateNewPayload(BlockParameter blockParameter)
        {
            SearchResult<Block> searchResult = _blockTree.SearchForBlock(blockParameter);
            if (searchResult.IsError)
            {
                throw new InvalidDataException(searchResult.Error);
            }

            Block? block = searchResult.Object;
            return GeneratePayloadFromBlock(block);
        }

        public ExecutionPayloadForRpc GenerateNewPayloadWithTransactions(TransactionForRpc[] transactions)
        {
            throw new NotImplementedException();
        }

        private ExecutionPayloadForRpc GeneratePayloadFromBlock(Block? block)
        {
            if (block is null)
            {
                throw new InvalidDataException("Block not found");
            }

            // Generate a new payload based on the block, use versioned ExecutionPayload based on spec
            IReleaseSpec spec = _specProvider.GetSpec(block.Header);

            ExecutionPayload executionPayload = spec.IsEip4844Enabled
                ? ExecutionPayloadV3.Create(block)
                : ExecutionPayload.Create(block);

            string engineEndpointVersion = executionPayload.GetExecutionPayloadVersion() switch
            {
                4 => "engine_newPayloadV4",
                3 => "engine_newPayloadV3",
                2 => "engine_newPayloadV2",
                1 => "engine_newPayloadV1",
                _ => throw new InvalidOperationException("Unsupported spec version")
            };

            return new ExecutionPayloadForRpc(engineEndpointVersion, executionPayload);
        }
    }
}
