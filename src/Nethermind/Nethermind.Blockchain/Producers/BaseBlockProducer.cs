﻿//  Copyright (c) 2018 Demerzel Solutions Limited
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
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Processing;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.State.Proofs;

namespace Nethermind.Blockchain.Producers
{
    public abstract class BaseBlockProducer : IBlockProducer
    {
        private IBlockchainProcessor Processor { get; }
        protected IBlockTree BlockTree { get; }
        protected IBlockProcessingQueue BlockProcessingQueue { get; }

        private readonly ISealer _sealer;
        private readonly IStateProvider _stateProvider;
        private readonly ITimestamper _timestamper;
        private readonly ITxSource _txSource;
        protected ILogger Logger { get; }

        protected BaseBlockProducer(
            ITxSource txSource,
            IBlockchainProcessor processor,
            ISealer sealer,
            IBlockTree blockTree,
            IBlockProcessingQueue blockProcessingQueue,
            IStateProvider stateProvider,
            ITimestamper timestamper,
            ILogManager logManager)
        {
            _txSource = txSource ?? throw new ArgumentNullException(nameof(txSource));
            Processor = processor ?? throw new ArgumentNullException(nameof(processor));
            _sealer = sealer ?? throw new ArgumentNullException(nameof(sealer));
            BlockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            BlockProcessingQueue = blockProcessingQueue ?? throw new ArgumentNullException(nameof(blockProcessingQueue));
            _stateProvider = stateProvider ?? throw new ArgumentNullException(nameof(stateProvider));
            _timestamper = timestamper ?? throw new ArgumentNullException(nameof(timestamper));
            Logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
        }

        public abstract void Start();

        public abstract Task StopAsync();

        private readonly object _newBlockLock = new object();

        protected Task<bool> TryProduceNewBlock(CancellationToken token)
        {
            lock (_newBlockLock)
            {
                BlockHeader parentHeader = BlockTree.Head?.Header;
                if (parentHeader == null)
                {
                    if (Logger.IsWarn) Logger.Warn($"Preparing new block - parent header is null");
                }
                else if (_sealer.CanSeal(parentHeader.Number + 1, parentHeader.Hash))
                {
                    return ProduceNewBlock(parentHeader, token);
                }

                return Task.FromResult(false);
            }
        }

        private Task<bool> ProduceNewBlock(BlockHeader parent, CancellationToken token)
        {
            _stateProvider.StateRoot = parent.StateRoot;
            Block block = PrepareBlock(parent);
            if (PreparedBlockCanBeMined(block))
            {
                var processedBlock = ProcessPreparedBlock(block);
                if (processedBlock == null)
                {
                    if (Logger.IsError) Logger.Error("Block prepared by block producer was rejected by processor.");
                }
                else
                {
                    return _sealer.SealBlock(processedBlock, token).ContinueWith((Func<Task<Block>, bool>) (t =>
                    {
                        if (t.IsCompletedSuccessfully)
                        {
                            if (t.Result != null)
                            {
                                if (Logger.IsInfo) Logger.Info($"Sealed block {t.Result.ToString(Block.Format.HashNumberDiffAndTx)}");
                                BlockTree.SuggestBlock(t.Result);
                                return true;
                            }
                            else
                            {
                                if (Logger.IsInfo) Logger.Info($"Failed to seal block {processedBlock.ToString(Block.Format.HashNumberDiffAndTx)} (null seal)");
                            }
                        }
                        else if (t.IsFaulted)
                        {
                            if (Logger.IsError) Logger.Error("Mining failed", t.Exception);
                        }
                        else if (t.IsCanceled)
                        {
                            if (Logger.IsInfo) Logger.Info($"Sealing block {processedBlock.Number} cancelled");
                        }

                        return false;
                    }), token);
                }
            }

            return Task.FromResult(false);
        }

        protected virtual Block ProcessPreparedBlock(Block block) => Processor.Process(block, ProcessingOptions.ProducingBlock, NullBlockTracer.Instance);

        protected virtual bool PreparedBlockCanBeMined(Block block)
        {
            if (block == null)
            {
                if (Logger.IsError) Logger.Error("Failed to prepare block for mining.");
                return false;
            }

            return true;
        }

        protected virtual Block PrepareBlock(BlockHeader parent)
        {
            UInt256 timestamp = _timestamper.EpochSeconds;
            UInt256 difficulty = CalculateDifficulty(parent, timestamp);
            BlockHeader header = new BlockHeader(
                parent.Hash,
                Keccak.OfAnEmptySequenceRlp,
                Address.Zero,
                difficulty,
                parent.Number + 1,
                GetGasLimit(parent),
                UInt256.Max(parent.Timestamp + 1, _timestamper.EpochSeconds),
                Encoding.UTF8.GetBytes("Nethermind"))
            {
                TotalDifficulty = parent.TotalDifficulty + difficulty
            };

            if (Logger.IsDebug) Logger.Debug($"Setting total difficulty to {parent.TotalDifficulty} + {difficulty}.");

            var transactions = _txSource.GetTransactions(parent, header.GasLimit);
            Block block = new Block(header, transactions, new BlockHeader[0]);
            header.TxRoot = new TxTrie(block.Transactions).RootHash;
            return block;
        }

        protected virtual long GetGasLimit(BlockHeader parent) => parent.GasLimit;

        protected abstract UInt256 CalculateDifficulty(BlockHeader parent, UInt256 timestamp);
    }
}