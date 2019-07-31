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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using Nethermind.Blockchain;
using Nethermind.Blockchain.TxPools;
using Nethermind.Blockchain.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;
using Nethermind.Mining;
using Nethermind.Store;

namespace Nethermind.Clique
{
    public class CliqueBlockProducer : ICliqueBlockProducer, IDisposable
    {
        private static readonly BigInteger MinGasPriceForMining = 1;
        private readonly IBlockTree _blockTree;
        private readonly IStateProvider _stateProvider;
        private readonly ITimestamper _timestamper;
        private readonly ILogger _logger;
        private readonly ICryptoRandom _cryptoRandom;
        private readonly WiggleRandomizer _wiggle;

        private readonly IBlockchainProcessor _processor;
        private readonly ITxPool _txPool;
        private ISealer _sealer;
        private readonly ISnapshotManager _snapshotManager;
        private ICliqueConfig _config;
        private Address _address;
        private ConcurrentDictionary<Address, bool> _proposals = new ConcurrentDictionary<Address, bool>();

        private CancellationTokenSource _cancellationTokenSource = new CancellationTokenSource();
        private System.Timers.Timer _timer = new System.Timers.Timer();

        public CliqueBlockProducer(ITxPool txPool,
            IBlockchainProcessor blockchainProcessor,
            IBlockTree blockTree,
            ITimestamper timestamper,
            ICryptoRandom cryptoRandom,
            IStateProvider stateProvider,
            ISnapshotManager snapshotManager,
            ISealer cliqueSealer,
            Address address,
            ICliqueConfig config,
            ILogManager logManager)
        {
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _txPool = txPool ?? throw new ArgumentNullException(nameof(txPool));
            _processor = blockchainProcessor ?? throw new ArgumentNullException(nameof(blockchainProcessor));
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _stateProvider = stateProvider ?? throw new ArgumentNullException(nameof(stateProvider));
            _timestamper = timestamper ?? throw new ArgumentNullException(nameof(timestamper));
            _cryptoRandom = cryptoRandom ?? throw new ArgumentNullException(nameof(cryptoRandom));
            _sealer = cliqueSealer ?? throw new ArgumentNullException(nameof(cliqueSealer));
            _snapshotManager = snapshotManager ?? throw new ArgumentNullException(nameof(snapshotManager));
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _address = address ?? throw new ArgumentNullException(nameof(address));
            _wiggle = new WiggleRandomizer(_cryptoRandom, _snapshotManager);

            _timer.AutoReset = false;
            _timer.Elapsed += TimerOnElapsed;
            _timer.Interval = 100;
            _timer.Start();
        }

        private readonly BlockingCollection<Block> _signalsQueue = new BlockingCollection<Block>(new ConcurrentQueue<Block>());

        private Block _scheduledBlock;

        public void CastVote(Address signer, bool vote)
        {
            bool success = _proposals.TryAdd(signer, vote);
            if (!success)
            {
                throw new InvalidOperationException($"A vote for {signer} has already been cast.");
            }

            if (_logger.IsWarn) _logger.Warn($"Added Clique vote for {signer} - {vote}");
        }

        public void UncastVote(Address signer)
        {
            bool success = _proposals.TryRemove(signer, out _);
            if (!success)
            {
                throw new InvalidOperationException("Cannot uncast vote");
            }

            if (_logger.IsWarn) _logger.Warn($"Removed Clique vote for {signer}");
        }

        private void TimerOnElapsed(object sender, ElapsedEventArgs e)
        {
            try
            {
                if (_blockTree.Head == null)
                {
                    _timer.Enabled = true;
                    return;
                }
                
                Block scheduledBlock = _scheduledBlock;
                if (scheduledBlock == null)
                {
                    if (_blockTree.Head.Timestamp + _config.BlockPeriod < _timestamper.EpochSeconds)
                    {
                        _signalsQueue.Add(_blockTree.FindBlock(_blockTree.Head.Hash, BlockTreeLookupOptions.None));
                    }

                    _timer.Enabled = true;
                    return;
                }

                string turnDescription = scheduledBlock.IsInTurn() ? "IN TURN" : "OUT OF TURN";
                
                int wiggle = _wiggle.WiggleFor(_scheduledBlock.Header);
                if (scheduledBlock.Timestamp * 1000 + (UInt256)wiggle < _timestamper.EpochMilliseconds)
                {
                    if (scheduledBlock.TotalDifficulty > _blockTree.Head.TotalDifficulty)
                    {
                        if(ReferenceEquals(scheduledBlock, _scheduledBlock))
                        {
                            BlockHeader parent = _blockTree.FindParentHeader(scheduledBlock.Header, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
                            Address parentSigner = _snapshotManager.GetBlockSealer(parent);
                            
                            string parentTurnDescription = parent.IsInTurn() ? "IN TURN" : "OUT OF TURN";
                            string parentDetails = $"{parentTurnDescription} {parent.TimestampDate:HH:mm:ss} {parent.ToString(BlockHeader.Format.Short)} sealed by {KnownAddresses.GetDescription(parentSigner)}";
                            
                            if (_logger.IsInfo) _logger.Info($"Suggesting own {turnDescription} {_scheduledBlock.TimestampDate:HH:mm:ss} {scheduledBlock.ToString(Block.Format.HashNumberDiffAndTx)} based on {parentDetails} after the delay of {wiggle}");
                            _blockTree.SuggestBlock(scheduledBlock);
                        }
                    }
                    else
                    {
                        if (_logger.IsInfo) _logger.Info($"Dropping a losing block {scheduledBlock.ToString(Block.Format.HashNumberDiffAndTx)}");
                    }

                    if (ReferenceEquals(scheduledBlock, _scheduledBlock))
                    {
                        _scheduledBlock = null;
                    }
                }
                else
                {
                    if (_logger.IsTrace) _logger.Trace($"Not yet {scheduledBlock.ToString(Block.Format.HashNumberDiffAndTx)}");
                }

                _timer.Enabled = true;
            }
            catch (Exception exception)
            {
                if (_logger.IsError) _logger.Error("Clique block producer failure", exception);
            }
        }

        private Task _producerTask;

        public void Start()
        {
            _blockTree.NewHeadBlock += BlockTreeOnNewHeadBlock;
            _producerTask = Task.Factory.StartNew(
                ConsumeSignal,
                _cancellationTokenSource.Token,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default).ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    if (_logger.IsError) _logger.Error("Clique block producer encountered an exception.", t.Exception);
                }
                else if (t.IsCanceled)
                {
                    if (_logger.IsDebug) _logger.Debug("Clique block producer stopped.");
                }
                else if (t.IsCompleted)
                {
                    if (_logger.IsDebug) _logger.Debug("Clique block producer complete.");
                }
            });
        }

        private void BlockTreeOnNewHeadBlock(object sender, BlockEventArgs e)
        {
            _signalsQueue.Add(e.Block);
        }

        private void ConsumeSignal()
        {
            foreach (Block signal in _signalsQueue.GetConsumingEnumerable(_cancellationTokenSource.Token))
            {
                Block parentBlock = signal;
                while (_signalsQueue.TryTake(out Block nextSignal))
                {
                    if (parentBlock.Number <= nextSignal.Number)
                    {
                        parentBlock = nextSignal;
                    }
                }

                try
                {
                    Block block = PrepareBlock(parentBlock);
                    if (block == null)
                    {
                        if (_logger.IsTrace) _logger.Trace("Skipping block production or block production failed");
                        continue;
                    }

                    if (_logger.IsInfo) _logger.Info($"Processing prepared block {block.Number}");
                    Block processedBlock = _processor.Process(block, ProcessingOptions.NoValidation | ProcessingOptions.ReadOnlyChain | ProcessingOptions.WithRollback, NullBlockTracer.Instance);
                    if (processedBlock == null)
                    {
                        if (_logger.IsInfo) _logger.Info($"Prepared block has lost the race");
                        continue;
                    }

                    if (_logger.IsDebug) _logger.Debug($"Sealing prepared block {processedBlock.Number}");

                    _sealer.SealBlock(processedBlock, _cancellationTokenSource.Token).ContinueWith(t =>
                    {
                        if (t.IsCompletedSuccessfully)
                        {
                            if (t.Result != null)
                            {
                                if (_logger.IsInfo) _logger.Info($"Sealed block {t.Result.ToString(Block.Format.HashNumberDiffAndTx)}");
                                _scheduledBlock = t.Result;
                            }
                            else
                            {
                                if (_logger.IsInfo) _logger.Info($"Failed to seal block {processedBlock.ToString(Block.Format.HashNumberDiffAndTx)} (null seal)");
                            }
                        }
                        else if (t.IsFaulted)
                        {
                            if (_logger.IsError) _logger.Error("Mining failed", t.Exception);
                        }
                        else if (t.IsCanceled)
                        {
                            if (_logger.IsInfo) _logger.Info($"Sealing block {processedBlock.Number} cancelled");
                        }
                    }, _cancellationTokenSource.Token);
                }
                catch (Exception e)
                {
                    if (_logger.IsError) _logger.Error($"Block producer could not produce block on top of {parentBlock.ToString(Block.Format.Short)}", e);
                }
            }
        }

        public async Task StopAsync()
        {
            _blockTree.NewHeadBlock -= BlockTreeOnNewHeadBlock;
            _cancellationTokenSource?.Cancel();
            await (_producerTask ?? Task.CompletedTask);
        }

        private Keccak _recentNotAllowedParent;

        private Block PrepareBlock(Block parentBlock)
        {
            BlockHeader parentHeader = parentBlock.Header;
            if (parentHeader == null)
            {
                if (_logger.IsError) _logger.Error($"Preparing new block on top of {parentBlock.ToString(Block.Format.Short)} - parent header is null");
                return null;
            }

            if (_recentNotAllowedParent == parentBlock.Hash)
            {
                return null;
            }

            if (!_sealer.CanSeal(parentHeader.Number + 1, parentHeader.Hash))
            {
                if (_logger.IsTrace) _logger.Trace($"Not allowed to sign block ({parentBlock.Number + 1})");
                _recentNotAllowedParent = parentHeader.Hash;
                return null;
            }

            if (_logger.IsInfo) _logger.Info($"Preparing new block on top of {parentBlock.ToString(Block.Format.Short)}");

            UInt256 timestamp = _timestamper.EpochSeconds;

            BlockHeader header = new BlockHeader(
                parentBlock.Hash,
                Keccak.OfAnEmptySequenceRlp,
                Address.Zero,
                1,
                parentBlock.Number + 1,
                parentBlock.GasLimit,
                timestamp > parentBlock.Timestamp ? timestamp : parentBlock.Timestamp + 1,
                new byte[0]);

            // If the block isn't a checkpoint, cast a random vote (good enough for now)
            long number = header.Number;
            // Assemble the voting snapshot to check which votes make sense
            Snapshot snapshot = _snapshotManager.GetOrCreateSnapshot(number - 1, header.ParentHash);
            bool isEpochBlock = (ulong) number % 30000 == 0;
            if (!isEpochBlock && _proposals.Any())
            {
                // Gather all the proposals that make sense voting on
                List<Address> addresses = new List<Address>();
                foreach (var proposal in _proposals)
                {
                    Address address = proposal.Key;
                    bool authorize = proposal.Value;
                    if (_snapshotManager.IsValidVote(snapshot, address, authorize))
                    {
                        addresses.Add(address);
                    }
                }

                // If there's pending proposals, cast a vote on them
                if (addresses.Count > 0)
                {
                    header.Beneficiary = addresses[_cryptoRandom.NextInt(addresses.Count)];
                    header.Nonce = _proposals[header.Beneficiary] ? Clique.NonceAuthVote : Clique.NonceDropVote;
                }
            }

            // Set the correct difficulty
            header.Difficulty = CalculateDifficulty(snapshot, _address);
            header.TotalDifficulty = parentBlock.TotalDifficulty + header.Difficulty;
            if (_logger.IsDebug) _logger.Debug($"Setting total difficulty to {parentBlock.TotalDifficulty} + {header.Difficulty}.");

            // Set extra data
            int mainBytesLength = Clique.ExtraVanityLength + Clique.ExtraSealLength;
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
                    int index = Clique.ExtraVanityLength + 20 * i;
                    Array.Copy(signer.Bytes, 0, header.ExtraData, index, signer.Bytes.Length);
                }
            }

            // Mix digest is reserved for now, set to empty
            header.MixHash = Keccak.Zero;
            // Ensure the timestamp has the correct delay
            header.Timestamp = parentBlock.Timestamp + _config.BlockPeriod;
            if (header.Timestamp < _timestamper.EpochSeconds)
            {
                header.Timestamp = new UInt256(_timestamper.EpochSeconds);
            }

            var transactions = _txPool.GetPendingTransactions().OrderBy(t => t.Nonce).ThenByDescending(t => t.GasPrice).ThenBy(t => t.GasLimit); // by nonce in case there are two transactions for the same account

            var selectedTxs = new List<Transaction>();
            BigInteger gasRemaining = header.GasLimit;

            if (_logger.IsDebug) _logger.Debug($"Collecting pending transactions at min gas price {MinGasPriceForMining} and block gas limit {gasRemaining}.");

            int total = 0;
            _stateProvider.StateRoot = parentHeader.StateRoot;

            Dictionary<Address, UInt256> nonces = new Dictionary<Address, UInt256>();
            foreach (Transaction transaction in transactions)
            {
                total++;
                if (transaction.SenderAddress == null)
                {
                    if (_logger.IsError) _logger.Error("Rejecting null sender pending transaction.");
                    continue;
                }

                if (transaction.GasPrice < MinGasPriceForMining)
                {
                    if (_logger.IsTrace) _logger.Trace($"Rejecting transaction - gas price ({transaction.GasPrice}) too low (min gas price: {MinGasPriceForMining}.");
                    continue;
                }

                if (transaction.Nonce != _stateProvider.GetNonce(transaction.SenderAddress) && (!nonces.ContainsKey(transaction.SenderAddress) || nonces[transaction.SenderAddress] + 1 != transaction.Nonce))
                {
                    if (_logger.IsTrace) _logger.Trace($"Rejecting transaction based on nonce.");
                    continue;
                }

                if (transaction.GasLimit > gasRemaining)
                {
                    if (_logger.IsTrace) _logger.Trace($"Rejecting transaction - gas limit ({transaction.GasPrice}) more than remaining gas ({gasRemaining}).");
                    continue;
                }

                BigInteger requiredBalance = transaction.GasLimit * (BigInteger)transaction.GasPrice + transaction.Value;
                BigInteger balance = _stateProvider.GetBalance(transaction.SenderAddress);
                if (requiredBalance > balance)
                {
                    if (_logger.IsTrace) _logger.Trace($"Rejecting transaction - insufficient balance - required {requiredBalance}, actual {balance}.");
                    continue;
                }

                selectedTxs.Add(transaction);
                nonces[transaction.SenderAddress] = transaction.Nonce;
                gasRemaining -= transaction.GasLimit;
            }

            if (_logger.IsDebug) _logger.Debug($"Collected {selectedTxs.Count} out of {total} pending transactions.");

            Block block = new Block(header, selectedTxs, new BlockHeader[0]);
            header.TxRoot = block.CalculateTxRoot();
            block.Author = _address;
            return block;
        }

        private UInt256 CalculateDifficulty(Snapshot snapshot, Address signer)
        {
            if (_snapshotManager.IsInTurn(snapshot, snapshot.Number + 1, signer))
            {
                if (_logger.IsInfo) _logger.Info("Producing in turn block");
                return new UInt256(Clique.DifficultyInTurn);
            }

            if (_logger.IsInfo) _logger.Info("Producing out of turn block");
            return new UInt256(Clique.DifficultyNoTurn);
        }

        public void Dispose()
        {
            _cancellationTokenSource?.Dispose();
            _timer?.Dispose();
        }
    }
}