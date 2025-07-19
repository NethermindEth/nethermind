// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Tracing.GethStyle;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Consensus.ExecutionRequests;
using Nethermind.Consensus.Tracing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.ExecutionRequest;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Data;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.Modules.DebugModule;
using Nethermind.Merge.Plugin.Data;
using Nethermind.State;
using Nethermind.Synchronization.ParallelSync;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.Merge.Plugin
{
    public class EngineDebugBridge : IEngineDebugBridge
    {
        private readonly IWorldState _worldState;
        private readonly IConfigProvider _configProvider;
        private IReadOnlyDbProvider _dbProvider;
        private IGethStyleTracer _tracer;
        private IBlockTree _blockTree;
        private IReceiptStorage _receiptStorage;
        private IReceiptsMigration _receiptsMigration;
        private ISpecProvider _specProvider;
        private ISyncModeSelector _syncModeSelector;
        private IBadBlockStore _badBlockStore;
        private IBlockProducer _blockProducer;
        private IJsonRpcConfig _jsonRpcConfig;

        public EngineDebugBridge(
            IConfigProvider configProvider,
            IReadOnlyDbProvider dbProvider,
            IGethStyleTracer tracer,
            IBlockTree blockTree,
            IReceiptStorage receiptStorage,
            IReceiptsMigration receiptsMigration,
            ISpecProvider specProvider,
            ISyncModeSelector syncModeSelector,
            IBadBlockStore badBlockStore,
            IBlockProducer blockProducer,
            IWorldState worldState,
            IJsonRpcConfig jsonRpcConfig)
        {
            _worldState = worldState ?? throw new ArgumentNullException(nameof(worldState));
            _configProvider = configProvider ?? throw new ArgumentNullException(nameof(configProvider));
            _dbProvider = dbProvider ?? throw new ArgumentNullException(nameof(dbProvider));
            _tracer = tracer ?? throw new ArgumentNullException(nameof(tracer));
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _receiptStorage = receiptStorage ?? throw new ArgumentNullException(nameof(receiptStorage));
            _receiptsMigration = receiptsMigration ?? throw new ArgumentNullException(nameof(receiptsMigration));
            _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
            _syncModeSelector = syncModeSelector ?? throw new ArgumentNullException(nameof(syncModeSelector));
            _badBlockStore = badBlockStore ?? throw new ArgumentNullException(nameof(badBlockStore));
            _blockProducer = blockProducer ?? throw new ArgumentNullException(nameof(blockProducer));
            _jsonRpcConfig = jsonRpcConfig ?? throw new ArgumentNullException(nameof(jsonRpcConfig));
        }

        public ExecutionPayloadForDebugRpc GenerateNewPayload(BlockParameter blockParameter)
        {
            SearchResult<Block> searchResult = _blockTree.SearchForBlock(blockParameter);
            if (searchResult.IsError)
            {
                throw new InvalidDataException(searchResult.Error);
            }

            Block? block = searchResult.Object;
            return GeneratePayloadFromBlock(block);
        }

        private bool GetExecutionRequestsFromReceiptsOfBlock(Block block)
        {
            using CancellationTokenSource? timeout = _jsonRpcConfig.BuildTimeoutCancellationToken();
            CancellationToken cancellationToken = timeout.Token;

            ExecuteBlock(block, _tracer, cancellationToken);
            if (block.ExecutionRequests is not null && block.ExecutionRequests.Any())
            {
                // If execution requests are already present, we don't need to process them again
                return true;
            }

            return true; // Execution requests were processed
        }

        private void ExecuteBlock(Block block, IGethStyleTracer tracer, CancellationToken cancellationToken)
        {
            _tracer.TraceBlock(block, GethTraceOptions.Default, cancellationToken);
        }

        public ExecutionPayloadForDebugRpc GenerateNewPayloadWithTransactions(TransactionForRpc[] transactions)
        {
            throw new NotImplementedException();
        }

        private ExecutionPayloadForDebugRpc GeneratePayloadFromBlock(Block? block)
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

            if(spec.RequestsEnabled)
            {
                // If execution requests are enabled, we need to process them from the receipts of the block
                if (GetExecutionRequestsFromReceiptsOfBlock(block))
                {
                    executionPayload.ExecutionRequests = block.ExecutionRequests;
                }
            }

            string engineEndpointVersion = executionPayload.GetExecutionPayloadVersion() switch
            {
                4 => "engine_newPayloadV4",
                3 => "engine_newPayloadV3",
                2 => "engine_newPayloadV2",
                1 => "engine_newPayloadV1",
                _ => throw new InvalidOperationException("Unsupported spec version")
            };

            return new ExecutionPayloadForDebugRpc(engineEndpointVersion, executionPayload);
        }

        public Hash256 CalculateBlockHash(ExecutionPayload executionPayload)
        {
            if (executionPayload == null)
            {
                throw new ArgumentNullException(nameof(executionPayload), "Execution payload cannot be null");
            }

            // Assuming we have a method to calculate the block hash from the execution payload
            BlockDecodingResult result = executionPayload.TryGetBlock();
            if (result.Block is null)
            {
                throw new InvalidDataException(result.Error);
            }
            Block block = result.Block;
            return block.Header.CalculateHash(); // Return the hash of the block header
        }
    }
}
