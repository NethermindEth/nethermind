/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */


using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.TransactionPools;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Logging;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Evm;
using Nethermind.Store;

namespace Nethermind.Clique
{
    public class CliqueBlockProducer : IBlockProducer
    {
        private static readonly BigInteger MinGasPriceForMining = 1;
        private readonly IBlockTree _blockTree;
        private readonly ITimestamp _timestamp;
        private readonly ILogger _logger;

        private readonly IBlockchainProcessor _processor;
        private readonly ITransactionPool _transactionPool;
        private CliqueSealEngine _sealEngine;
        private CliqueConfig _config;
        private Address _address;
        private Dictionary<Address, bool> _proposals = new Dictionary<Address, bool>();

        private object _syncToken = new object();
        private CancellationTokenSource _cancellationTokenSource = new CancellationTokenSource();

        public CliqueBlockProducer(
            ITransactionPool transactionPool,
            IBlockchainProcessor devProcessor,
            IBlockTree blockTree,
            ITimestamp timestamp,
            CliqueSealEngine cliqueSealEngine,
            CliqueConfig config,
            Address address,
            ILogManager logManager)
        {
            _transactionPool = transactionPool ?? throw new ArgumentNullException(nameof(transactionPool));
            _processor = devProcessor ?? throw new ArgumentNullException(nameof(devProcessor));
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _timestamp = timestamp;
            _sealEngine = cliqueSealEngine;
            _config = config;
            _blockTree = blockTree;
            _address = address;
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
        }

        public void Start()
        {
            _processor.ProcessingQueueEmpty += OnBlockProcessorQueueEmpty;
            _blockTree.NewBestSuggestedBlock += BlockTreeOnNewBestSuggestedBlock;
        }

        public async Task StopAsync()
        {
            _processor.ProcessingQueueEmpty -= OnBlockProcessorQueueEmpty;
            _blockTree.NewBestSuggestedBlock -= BlockTreeOnNewBestSuggestedBlock;
            lock (_syncToken)
            {
                _cancellationTokenSource?.Cancel();
            }
            
            await Task.CompletedTask;
        }

        private Block PrepareBlock()
        {
            BlockHeader parentHeader = _blockTree.Head;
            if (parentHeader == null) return null;

            Block parent = _blockTree.FindBlock(parentHeader.Hash, false);
            UInt256 timestamp = _timestamp.EpochSeconds;

            BlockHeader header = new BlockHeader(
                parent.Hash,
                Keccak.OfAnEmptySequenceRlp,
                Address.Zero,
                1,
                parent.Number + 1,
                parent.GasLimit,
                timestamp > parent.Timestamp ? timestamp : parent.Timestamp + 1,
                new byte[0]);

            // If the block isn't a checkpoint, cast a random vote (good enough for now)
            UInt256 number = header.Number;
            // Assemble the voting snapshot to check which votes make sense
            Snapshot snapshot = _sealEngine.GetOrCreateSnapshot(number - 1, header.ParentHash);
            bool isEpochBlock = (ulong)number % 30000 == 0;
            if (!isEpochBlock)
            {
                // Gather all the proposals that make sense voting on
                List<Address> addresses = new List<Address>();
                foreach (var proposal in _proposals)
                {
                    Address address = proposal.Key;
                    bool authorize = proposal.Value;
                    if (snapshot.ValidVote(address, authorize))
                    {
                        addresses.Append(address);
                    }
                }

                // If there's pending proposals, cast a vote on them
                if (addresses.Count > 0)
                {
                    Random rnd = new Random();
                    header.Beneficiary = addresses[rnd.Next(addresses.Count)];
                    if (_proposals[header.Beneficiary])
                    {
                        header.Nonce = CliqueSealEngine.NonceAuthVote;
                    }
                    else
                    {
                        header.Nonce = CliqueSealEngine.NonceDropVote;
                    }
                }
            }

            // Set the correct difficulty
            header.Difficulty = CalculateDifficulty(snapshot, _address);
            header.TotalDifficulty = parent.TotalDifficulty + header.Difficulty;
            if (_logger.IsDebug) _logger.Debug($"Setting total difficulty to {parent.TotalDifficulty} + {header.Difficulty}.");

            // Set extra data
            int mainBytesLength = CliqueSealEngine.ExtraVanityLength + CliqueSealEngine.ExtraSealLength;
            int signerBytesLength = isEpochBlock ? 20 * snapshot.Signers.Count : 0;
            int extraDataLength = mainBytesLength + signerBytesLength;
            header.ExtraData = new byte[extraDataLength];

            byte[] clientName = Encoding.UTF8.GetBytes("Nethermind");
            Array.Copy(clientName, header.ExtraData, clientName.Length);

            if (isEpochBlock)
            {
                for (int i = 0; i < snapshot.Signers.Keys.Count; i++)
                {
                    Address signer = snapshot.Signers.Keys[i];
                    int index = CliqueSealEngine.ExtraVanityLength + 20 * i;
                    Array.Copy(signer.Bytes, 0, header.ExtraData, index, signer.Bytes.Length);
                }
            }

            // Mix digest is reserved for now, set to empty
            header.MixHash = Keccak.Zero;
            // Ensure the timestamp has the correct delay
            header.Timestamp = parent.Timestamp + _config.BlockPeriod;
            long currentTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (header.Timestamp < currentTimestamp)
            {
                header.Timestamp = new UInt256(currentTimestamp);
            }

            var transactions = _transactionPool.GetPendingTransactions().OrderBy(t => t?.Nonce); // by nonce in case there are two transactions for the same account

            var selectedTxs = new List<Transaction>();
            BigInteger gasRemaining = header.GasLimit;

            if (_logger.IsDebug) _logger.Debug($"Collecting pending transactions at min gas price {MinGasPriceForMining} and block gas limit {gasRemaining}.");

            int total = 0;
            foreach (Transaction transaction in transactions)
            {
                total++;
                if (transaction == null) throw new InvalidOperationException("Block transaction is null");

                if (transaction.GasPrice < MinGasPriceForMining)
                {
                    if (_logger.IsTrace) _logger.Trace($"Rejecting transaction - gas price ({transaction.GasPrice}) too low (min gas price: {MinGasPriceForMining}.");
                    continue;
                }

                if (transaction.GasLimit > gasRemaining)
                {
                    if (_logger.IsTrace) _logger.Trace($"Rejecting transaction - gas limit ({transaction.GasPrice}) more than remaining gas ({gasRemaining}).");
                    break;
                }

                selectedTxs.Add(transaction);
                gasRemaining -= transaction.GasLimit;
            }

            if (_logger.IsDebug) _logger.Debug($"Collected {selectedTxs.Count} out of {total} pending transactions.");


            Block block = new Block(header, selectedTxs, new BlockHeader[0]);
            header.TransactionsRoot = block.CalculateTransactionsRoot();
            return block;
        }

        private void OnBlockProcessorQueueEmpty(object sender, EventArgs e)
        {
            CancellationToken token;
            lock (_syncToken)
            {
                _cancellationTokenSource = new CancellationTokenSource();
                token = _cancellationTokenSource.Token;
            }

            if (!_sealEngine.IsMining)
            {
                return;
            }
            
            Block block = PrepareBlock();
            if (block == null)
            {
                if (_logger.IsError) _logger.Error("Failed to prepare block for mining.");
                return;
            }

            Block processedBlock = _processor.Process(block, ProcessingOptions.NoValidation | ProcessingOptions.ReadOnlyChain | ProcessingOptions.WithRollback, NullTraceListener.Instance);
            _sealEngine.MineAsync(processedBlock, token).ContinueWith(t =>
            {
                if (t.IsCompletedSuccessfully)
                {
                    _blockTree.SuggestBlock(t.Result);
                }
                else if(t.IsFaulted)
                {
                    _logger.Error("Mining failer", t.Exception);
                }
                else if(t.IsCanceled)
                {
                    if(_logger.IsDebug) _logger.Debug($"Mining block {processedBlock.ToString(Block.Format.HashAndNumber)} cancelled");
                }
            }, token);
        }

        private void BlockTreeOnNewBestSuggestedBlock(object sender, BlockEventArgs e)
        {
            lock (_syncToken)
            {
                _cancellationTokenSource?.Cancel();
            }
        }

        private UInt256 CalculateDifficulty(Snapshot snapshot, Address signer)
        {
            if (snapshot.InTurn(snapshot.Number + 1, signer))
            {
                return new UInt256(CliqueSealEngine.DifficultyInTurn);
            }

            return new UInt256(CliqueSealEngine.DifficultyNoTurn);
        }
    }
}