// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Tracing;
using Nethermind.Blockchain.Tracing.GethStyle;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Consensus.ExecutionRequests;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Tracing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.ExecutionRequest;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
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
        private IBlockchainProcessor _blockchainProcessor;
        private IBlockTree _blockTree;
        private ISpecProvider _specProvider;

        public EngineDebugBridge(
            IBlockchainProcessor blockchainProcessor,
            IBlockTree blockTree,
            ISpecProvider specProvider)
        {
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _blockchainProcessor = blockchainProcessor ?? throw new ArgumentNullException(nameof(blockchainProcessor));
            _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
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
            ExecuteBlock(block);
            if (block.ExecutionRequests is not null && block.ExecutionRequests.Any())
            {
                // If execution requests are already present, we don't need to process them again
                return true;
            }

            return true; // Execution requests were processed
        }

        private void ExecuteBlock(Block block)
        {
            ArgumentNullException.ThrowIfNull(block);

            if (!block.IsGenesis)
            {
                BlockHeader? parent = _blockTree.FindParentHeader(block.Header, BlockTreeLookupOptions.None);
                if (parent?.Hash is null)
                {
                    throw new InvalidOperationException("Cannot trace blocks with invalid parents");
                }

                if (!_blockTree.IsMainChain(parent.Hash)) throw new InvalidOperationException("Cannot trace orphaned blocks");
            }

            _blockchainProcessor.Process(block, ProcessingOptions.ReadOnlyChain, NullBlockTracer.Instance);
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

            int payloadVersion = executionPayload.GetExecutionPayloadVersion();

            string engineEndpointVersion = $"engine_newPayloadV{payloadVersion}";

            byte[]?[]? blobVersionedHashes = executionPayload.TryGetTransactions().Transactions
                .Where(t => t.BlobVersionedHashes is not null)
                .SelectMany(t => t.BlobVersionedHashes!)
                .ToArray();

            Hash256? parentBeaconBlockRoot = block.ParentBeaconBlockRoot;
            byte[][]? executionRequests = executionPayload.ExecutionRequests;

            Params arguments = payloadVersion switch
            {
                1 or 2 => new ParamsV1(executionPayload),
                3 => new ParamsV3(executionPayload, blobVersionedHashes, parentBeaconBlockRoot),
                4 => new ParamsV4(executionPayload, blobVersionedHashes, parentBeaconBlockRoot, executionRequests),
                _ => throw new NotSupportedException($"Unsupported ExecutionPayload version: {payloadVersion}")
            };

            return new ExecutionPayloadForDebugRpc(engineEndpointVersion, arguments);
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
