// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Find;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.Mev.Data;
using Nethermind.Mev.Execution;
using Nethermind.Mev.Source;
using Nethermind.Serialization.Rlp;
using Nethermind.State;
using Nethermind.Trie;

namespace Nethermind.Mev
{
    public class MevRpcModule : IMevRpcModule
    {
        private readonly IJsonRpcConfig _jsonRpcConfig;
        private readonly IBundlePool _bundlePool;
        private readonly IBlockFinder _blockFinder;
        private readonly IStateReader _stateReader;
        private readonly ITracerFactory _tracerFactory;
        private readonly ISpecProvider _specProvider;
        private readonly ISigner? _signer;
        private readonly BlockValidationService _blockValidationService;

        static MevRpcModule()
        {
            Rlp.RegisterDecoders(typeof(BundleTxDecoder).Assembly);
        }

        public MevRpcModule(
            IJsonRpcConfig jsonRpcConfig,
            IBundlePool bundlePool,
            IBlockFinder blockFinder,
            IStateReader stateReader,
            ITracerFactory tracerFactory,
            ISpecProvider specProvider,
            ISigner? signer,
            BlockValidationService blockValidationService)
        {
            _jsonRpcConfig = jsonRpcConfig;
            _bundlePool = bundlePool;
            _blockFinder = blockFinder;
            _stateReader = stateReader;
            _tracerFactory = tracerFactory;
            _specProvider = specProvider;
            _signer = signer;
            _blockValidationService = blockValidationService;
        }

        [JsonRpcMethod(Description = "Validates a builder submission v1", IsImplemented = true)]
        public ResultWrapper<bool> mev_validateBuilderSubmissionV1(BuilderBlockValidationRequest request)
        {
            Block builderBlock = ValidateRequestAndGetBlock(request);

            _blockValidationService.ValidateBuilderSubmission(builderBlock, request.Message!,
                request.RegisteredGasLimit);
            return ResultWrapper<bool>.Success(true);
        }

        [JsonRpcMethod(Description = "Validates a builder submission v2", IsImplemented = true)]
        public ResultWrapper<bool> mev_validateBuilderSubmissionV2(BuilderBlockValidationRequest request)
        {
            Block builderBlock = ValidateRequestAndGetBlock(request);

            _blockValidationService.ValidateBuilderSubmission(builderBlock, request.Message!,
                request.RegisteredGasLimit, request.WithdrawalsRoot);
            return ResultWrapper<bool>.Success(true);
        }

        private static Block ValidateRequestAndGetBlock(BuilderBlockValidationRequest request)
        {
            if (request.Message is null)
            {
                throw new InvalidOperationException("Message is null");
            }

            if (request.ExecutionPayload is null)
            {
                throw new InvalidOperationException("Execution Payload is null");
            }

            if (!request.ExecutionPayload.TryGetBlock(out Block? builderBlock))
            {
                throw new InvalidOperationException("Execution Payload failed to be converted to Block");
            }

            return builderBlock;
        }

        public ResultWrapper<bool> eth_sendBundle(MevBundleRpc mevBundleRpc)
        {
            BundleTransaction[] txs = Decode(mevBundleRpc.Txs, mevBundleRpc.RevertingTxHashes?.ToHashSet());
            MevBundle bundle = new(mevBundleRpc.BlockNumber, txs, mevBundleRpc.MinTimestamp, mevBundleRpc.MaxTimestamp);
            bool result = _bundlePool.AddBundle(bundle);
            return ResultWrapper<bool>.Success(result);
        }

        public ResultWrapper<bool> eth_sendMegabundle(MevMegabundleRpc mevMegabundleRpc)
        {
            BundleTransaction[] txs = Decode(mevMegabundleRpc.Txs, mevMegabundleRpc.RevertingTxHashes?.ToHashSet());
            Signature relaySignature = new(mevMegabundleRpc.RelaySignature);
            MevMegabundle megabundle = new(mevMegabundleRpc.BlockNumber, txs, mevMegabundleRpc.RevertingTxHashes,
                relaySignature, mevMegabundleRpc.MinTimestamp, mevMegabundleRpc.MaxTimestamp);
            bool result = _bundlePool.AddMegabundle(megabundle);
            return ResultWrapper<bool>.Success(result);
        }

        public ResultWrapper<TxsResults> eth_callBundle(MevCallBundleRpc mevBundleRpc)
        {
            BundleTransaction[] txs = Decode(mevBundleRpc.Txs);
            return CallBundle(txs, mevBundleRpc.BlockNumber, mevBundleRpc.StateBlockNumber, mevBundleRpc.Timestamp);
        }

        private ResultWrapper<TxsResults> CallBundle(BundleTransaction[] txs, long? blockNumber, BlockParameter stateBlockNumber, ulong? timestamp)
        {
            if (txs.Length == 0)
                return ResultWrapper<TxsResults>.Fail("no tx specified in bundle");

            SearchResult<BlockHeader> searchResult = _blockFinder.SearchForHeader(stateBlockNumber);
            if (searchResult.IsError)
            {
                return ResultWrapper<TxsResults>.Fail(searchResult);
            }

            BlockHeader header = searchResult.Object!;
            if (!HasStateForBlock(header))
            {
                return ResultWrapper<TxsResults>.Fail($"No state available for block {header.Hash}", ErrorCodes.ResourceUnavailable);
            }

            const int callBundleTimeout = 5000;
            using CancellationTokenSource cancellationTokenSource = new(callBundleTimeout);
            long bundleBlockNumber = blockNumber ?? header.Number + 1;

            TxsResults results = new CallTxBundleExecutor(_tracerFactory, _specProvider, _signer).ExecuteBundle(
                new MevBundle(bundleBlockNumber, txs, timestamp, timestamp),
                header,
                cancellationTokenSource.Token,
                timestamp);

            return ResultWrapper<TxsResults>.Success(results);
        }

        private static BundleTransaction[] Decode(byte[][] transactions, ISet<Keccak>? revertingTxHashes = null)
        {
            revertingTxHashes ??= new HashSet<Keccak>();
            BundleTransaction[] txs = new BundleTransaction[transactions.Length];
            for (int i = 0; i < transactions.Length; i++)
            {
                BundleTransaction bundleTransaction = Rlp.Decode<BundleTransaction>(transactions[i], RlpBehaviors.SkipTypedWrapping);
                Keccak transactionHash = bundleTransaction.Hash!;
                bundleTransaction.CanRevert = revertingTxHashes.Contains(transactionHash);
                revertingTxHashes.Remove(transactionHash);

                txs[i] = bundleTransaction;
            }

            if (revertingTxHashes.Count > 0)
            {
                throw new ArgumentException(
                    $"Bundle didn't contain some of revertingTxHashes: [{string.Join(", ", revertingTxHashes.OfType<object>())}]",
                    nameof(revertingTxHashes));
            }

            return txs;
        }

        private bool HasStateForBlock(BlockHeader header)
        {
            RootCheckVisitor rootCheckVisitor = new();
            if (header.StateRoot is null) return false;
            _stateReader.RunTreeVisitor(rootCheckVisitor, header.StateRoot!);
            return rootCheckVisitor.HasRoot;
        }
    }
}
