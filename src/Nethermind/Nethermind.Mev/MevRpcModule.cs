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

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Find;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Data;
using Nethermind.Int256;
using Nethermind.Core;
using Nethermind.Consensus;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.JsonRpc.Modules;
using Nethermind.Mev.Data;
using Nethermind.Mev.Execution;
using Nethermind.Mev.Source;
using Nethermind.Serialization.Rlp;
using Nethermind.State;
using Nethermind.Trie;
using Nethermind.TxPool;

namespace Nethermind.Mev
{
    public class MevRpcModule : IMevRpcModule
    {
        private readonly IJsonRpcConfig _jsonRpcConfig;
        private readonly IBundlePool _bundlePool;
        private readonly ITxSender _txSender;
        private readonly IBlockFinder _blockFinder;
        private readonly IStateReader _stateReader;
        private readonly ITracerFactory _tracerFactory;
        private readonly IEciesCipher _cipher;
        private readonly ISpecProvider _specProvider;
        private readonly ISigner? _signer;
        private readonly TxDecoder _txDecoder = new();

        static MevRpcModule()
        {
            Rlp.RegisterDecoders(typeof(BundleTxDecoder).Assembly);
        }

        public MevRpcModule(IJsonRpcConfig jsonRpcConfig,
            IBundlePool bundlePool,
            ITxSender txSender,
            IBlockFinder blockFinder,
            IStateReader stateReader,
            ITracerFactory tracerFactory,
            IEciesCipher? cipher,
            ISpecProvider specProvider,
            ISigner? signer)
        {
            _jsonRpcConfig = jsonRpcConfig;
            _bundlePool = bundlePool;
            _txSender = txSender ?? throw new ArgumentNullException(nameof(txSender));
            _blockFinder = blockFinder;
            _stateReader = stateReader;
            _tracerFactory = tracerFactory;
            _cipher = cipher ?? throw new ArgumentNullException(nameof(cipher));
            _specProvider = specProvider;
            _signer = signer;
        }

public ResultWrapper<bool> eth_sendBundle(MevBundleRpc mevBundleRpc)
        {
            BundleTransaction[] txs = Decode(mevBundleRpc.Txs, mevBundleRpc.RevertingTxHashes?.ToHashSet());
            MevBundle bundle = new(mevBundleRpc.BlockNumber, txs, mevBundleRpc.MinTimestamp, mevBundleRpc.MaxTimestamp);
            bool result = _bundlePool.AddBundle(bundle);
            return ResultWrapper<bool>.Success(result);
        }

        public ResultWrapper<TxsResults> eth_callBundle(MevCallBundleRpc mevBundleRpc)
        {
            BundleTransaction[] txs = Decode(mevBundleRpc.Txs);
            return CallBundle(txs, mevBundleRpc.BlockNumber, mevBundleRpc.StateBlockNumber, mevBundleRpc.Timestamp);
        }

        private ResultWrapper<TxsResults> CallBundle(BundleTransaction[] txs, long? blockNumber, BlockParameter blockParameter, UInt256? timestamp)
        {
            if (txs.Length == 0)
                return ResultWrapper<TxsResults>.Fail("no tx specified in bundle");

            SearchResult<BlockHeader> searchResult = _blockFinder.SearchForHeader(blockParameter);
            if (searchResult.IsError)
            {
                return ResultWrapper<TxsResults>.Fail(searchResult);
            }

            BlockHeader header = searchResult.Object!;
            if (blockNumber is not null)
            {
                header.Number = blockNumber.Value - 1;
            }

            if (!HasStateForBlock(header!))
            {
                return ResultWrapper<TxsResults>.Fail($"No state available for block {header.Hash}",
                    ErrorCodes.ResourceUnavailable);
            }

            using CancellationTokenSource cancellationTokenSource = new(_jsonRpcConfig.Timeout);

            TxsResults results = new CallTxBundleExecutor(_tracerFactory, _specProvider, _signer).ExecuteBundle(
                new MevBundle(header.Number, txs, timestamp, timestamp),
                header,
                cancellationTokenSource.Token,
                timestamp);

            return ResultWrapper<TxsResults>.Success(results);
        }

        public async Task<ResultWrapper<Keccak>> eth_publishBundle(
            PublicKey targetValidator,
            TransactionForRpc carrier,
            TransactionForRpc[] bundle)
        {
            if (bundle.Length != 1)
            {
                throw new NotImplementedException("There can be only one.");
            }

            Rlp mevTxRlp = _txDecoder.Encode(bundle[0].ToTransaction());
            byte[] ciphertext = _cipher.Encrypt(targetValidator, mevTxRlp.Bytes);
            
            Transaction carrierTx = carrier.ToTransaction();
            byte[] mevPrefix = new byte[0];
            carrierTx.Data = Bytes.Concat(mevPrefix, targetValidator.Bytes, ciphertext); // here we could encrypt for multiple validators possibly
            (Keccak? hash, AddTxResult? addTxResult) = await _txSender.SendTx(carrierTx, TxHandlingOptions.ManagedNonce);
            if (addTxResult != AddTxResult.Added)
            {
                return ResultWrapper<Keccak>.Fail(addTxResult.ToString()!, ErrorCodes.InternalError);
            }

            return ResultWrapper<Keccak>.Success(hash!);
        }

        private static BundleTransaction[] Decode(byte[][] transactions, ISet<Keccak>? revertingTxHashes = null)
        {
            revertingTxHashes ??= new HashSet<Keccak>();
            BundleTransaction[] txs = new BundleTransaction[transactions.Length];
            for (int i = 0; i < transactions.Length; i++)
            {
                BundleTransaction bundleTransaction = Rlp.Decode<BundleTransaction>(transactions[i]);
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
            if (header.StateRoot == null) return false;
            _stateReader.RunTreeVisitor(rootCheckVisitor, header.StateRoot!);
            return rootCheckVisitor.HasRoot;
        }
    }
}
