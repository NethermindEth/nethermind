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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Processing;
using Nethermind.Blockchain.Producers;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.State.Proofs;

namespace Nethermind.Consensus.Clique
{
    public class CliqueBlockProducer : ICliqueBlockProducer, IDisposable
    {
        private readonly IBlockTree _blockTree;
        private readonly IStateProvider _stateProvider;
        private readonly ITimestamper _timestamper;
        private readonly ILogger _logger;
        private readonly ICryptoRandom _cryptoRandom;
        private readonly WiggleRandomizer _wiggle;

        private readonly ITxSource _txSource;
        private readonly IBlockchainProcessor _processor;
        private readonly ISealer _sealer;
        private readonly IGasLimitCalculator _gasLimitCalculator;
        private readonly ISpecProvider _specProvider;
        private readonly ISnapshotManager _snapshotManager;
        private readonly ICliqueConfig _config;
        
        private readonly ConcurrentDictionary<Address, bool> _proposals = new();

        private readonly CancellationTokenSource _cancellationTokenSource = new();
        private readonly System.Timers.Timer _timer = new();
        private DateTime _lastProducedBlock;

        public CliqueBlockProducer(
            ITxSource txSource,
            IBlockchainProcessor blockchainProcessor,
            IStateProvider stateProvider,
            IBlockTree blockTree,
            ITimestamper timestamper,
            ICryptoRandom cryptoRandom,
            ISnapshotManager snapshotManager,
            ISealer cliqueSealer,
            IGasLimitCalculator gasLimitCalculator,
            ISpecProvider? specProvider,
            ICliqueConfig config,
            ILogManager logManager)
        {
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _txSource = txSource ?? throw new ArgumentNullException(nameof(txSource));
            _processor = blockchainProcessor ?? throw new ArgumentNullException(nameof(blockchainProcessor));
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _stateProvider = stateProvider ?? throw new ArgumentNullException(nameof(stateProvider));
            _timestamper = timestamper ?? throw new ArgumentNullException(nameof(timestamper));
            _cryptoRandom = cryptoRandom ?? throw new ArgumentNullException(nameof(cryptoRandom));
            _sealer = cliqueSealer ?? throw new ArgumentNullException(nameof(cliqueSealer));
            _gasLimitCalculator = gasLimitCalculator ?? throw new ArgumentNullException(nameof(gasLimitCalculator));
            _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
            _snapshotManager = snapshotManager ?? throw new ArgumentNullException(nameof(snapshotManager));
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _wiggle = new WiggleRandomizer(_cryptoRandom, _snapshotManager);

            _timer.AutoReset = false;
            _timer.Elapsed += TimerOnElapsed;
            _timer.Interval = 100;
            _timer.Start();
        }

        private readonly BlockingCollection<Block> _signalsQueue =
            new(new ConcurrentQueue<Block>());

        private Block? _scheduledBlock;

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

        public void ProduceOnTopOf(Keccak hash)
        {
            _signalsQueue.Add(_blockTree.FindBlock(hash, BlockTreeLookupOptions.None));
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

                Block? scheduledBlock = _scheduledBlock;
                if (scheduledBlock == null)
                {
                    if (_blockTree.Head.Timestamp + _config.BlockPeriod < _timestamper.UnixTime.Seconds)
                    {
                        _signalsQueue.Add(_blockTree.FindBlock(_blockTree.Head.Hash, BlockTreeLookupOptions.None));
                    }

                    _timer.Enabled = true;
                    return;
                }

                string turnDescription = scheduledBlock.IsInTurn() ? "IN TURN" : "OUT OF TURN";

                int wiggle = _wiggle.WiggleFor(scheduledBlock.Header);
                if (scheduledBlock.Timestamp * 1000 + (UInt256)wiggle < _timestamper.UnixTime.Milliseconds)
                {
                    if (scheduledBlock.TotalDifficulty > _blockTree.Head.TotalDifficulty)
                    {
                        if (ReferenceEquals(scheduledBlock, _scheduledBlock))
                        {
                            BlockHeader parent = _blockTree.FindParentHeader(scheduledBlock.Header,
                                BlockTreeLookupOptions.TotalDifficultyNotNeeded);
                            Address parentSigner = _snapshotManager.GetBlockSealer(parent);

                            string parentTurnDescription = parent.IsInTurn() ? "IN TURN" : "OUT OF TURN";
                            string parentDetails =
                                $"{parentTurnDescription} {parent.TimestampDate:HH:mm:ss} {parent.ToString(BlockHeader.Format.Short)} sealed by {KnownAddresses.GetDescription(parentSigner)}";

                            if (_logger.IsInfo)
                                _logger.Info(
                                    $"Suggesting own {turnDescription} {_scheduledBlock.TimestampDate:HH:mm:ss} {scheduledBlock.ToString(Block.Format.HashNumberDiffAndTx)} based on {parentDetails} after the delay of {wiggle}");
                            BlockProduced?.Invoke(this, new BlockEventArgs(scheduledBlock));
                        }
                    }
                    else
                    {
                        if (_logger.IsInfo)
                            _logger.Info(
                                $"Dropping a losing block {scheduledBlock.ToString(Block.Format.HashNumberDiffAndTx)}");
                    }

                    if (ReferenceEquals(scheduledBlock, _scheduledBlock))
                    {
                        _scheduledBlock = null;
                    }
                }
                else
                {
                    if (_logger.IsTrace)
                        _logger.Trace($"Not yet {scheduledBlock.ToString(Block.Format.HashNumberDiffAndTx)}");
                }

                _timer.Enabled = true;
            }
            catch (Exception exception)
            {
                if (_logger.IsError) _logger.Error("Clique block producer failure", exception);
            }
        }

        private Task? _producerTask;

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

        private void BlockTreeOnNewHeadBlock(object? sender, BlockEventArgs e)
        {
            _signalsQueue.Add(e.Block);
        }

        private void ConsumeSignal()
        {
            _lastProducedBlock = DateTime.UtcNow;
            foreach (Block signal in _signalsQueue.GetConsumingEnumerable(_cancellationTokenSource.Token))
            {
                Block parentBlock = signal;
                while (_signalsQueue.TryTake(out Block? nextSignal))
                {
                    if (parentBlock.Number <= nextSignal.Number)
                    {
                        parentBlock = nextSignal;
                    }
                }

                try
                {
                    Block? block = PrepareBlock(parentBlock);
                    if (block is null)
                    {
                        if (_logger.IsTrace) _logger.Trace("Skipping block production or block production failed");
                        Metrics.FailedBlockSeals++;
                        continue;
                    }

                    if (_logger.IsInfo) _logger.Info($"Processing prepared block {block.Number}");
                    Block? processedBlock = _processor.Process(
                        block,
                        ProcessingOptions.ProducingBlock,
                        NullBlockTracer.Instance);
                    if (processedBlock is null)
                    {
                        if (_logger.IsInfo) _logger.Info($"Prepared block has lost the race");
                        Metrics.FailedBlockSeals++;
                        continue;
                    }

                    if (_logger.IsDebug) _logger.Debug($"Sealing prepared block {processedBlock.Number}");

                    _sealer.SealBlock(processedBlock, _cancellationTokenSource.Token).ContinueWith(t =>
                    {
                        if (t.IsCompletedSuccessfully)
                        {
                            if (t.Result != null)
                            {
                                if (_logger.IsInfo)
                                    _logger.Info($"Sealed block {t.Result.ToString(Block.Format.HashNumberDiffAndTx)}");
                                _scheduledBlock = t.Result;
                                _lastProducedBlock = DateTime.UtcNow;
                                Metrics.BlocksSealed++;
                            }
                            else
                            {
                                if (_logger.IsInfo)
                                    _logger.Info(
                                        $"Failed to seal block {processedBlock.ToString(Block.Format.HashNumberDiffAndTx)} (null seal)");
                                Metrics.FailedBlockSeals++;
                            }
                        }
                        else if (t.IsFaulted)
                        {
                            if (_logger.IsError) _logger.Error("Mining failed", t.Exception);
                            Metrics.FailedBlockSeals++;
                        }
                        else if (t.IsCanceled)
                        {
                            if (_logger.IsInfo) _logger.Info($"Sealing block {processedBlock.Number} cancelled");
                            Metrics.FailedBlockSeals++;
                        }
                    }, _cancellationTokenSource.Token);
                }
                catch (Exception e)
                {
                    if (_logger.IsError)
                        _logger.Error(
                            $"Block producer could not produce block on top of {parentBlock.ToString(Block.Format.Short)}",
                            e);
                    Metrics.FailedBlockSeals++;
                }
            }
        }

        public async Task StopAsync()
        {
            _blockTree.NewHeadBlock -= BlockTreeOnNewHeadBlock;
            _cancellationTokenSource?.Cancel();
            await (_producerTask ?? Task.CompletedTask);
        }

        bool IBlockProducer.IsProducingBlocks(ulong? maxProducingInterval)
        {
            if (_producerTask == null || _producerTask.IsCompleted)
                return false;
            if (maxProducingInterval != null)
                return _lastProducedBlock.AddSeconds(maxProducingInterval.Value) > DateTime.UtcNow;
            else
                return true;
        }

        public ITimestamper Timestamper => _timestamper;
        public event EventHandler<BlockEventArgs>? BlockProduced;

        private Keccak? _recentNotAllowedParent;

        private Block? PrepareBlock(Block parentBlock)
        {
            BlockHeader parentHeader = parentBlock.Header;
            if (parentHeader.Hash == null)
            {
                if (_logger.IsError) _logger.Error(
                    $"Preparing new block on top of {parentHeader.ToString(BlockHeader.Format.Short)} - parent header hash is null");
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

            if (_logger.IsInfo)
                _logger.Info($"Preparing new block on top of {parentBlock.ToString(Block.Format.Short)}");

            UInt256 timestamp = _timestamper.UnixTime.Seconds;
            IReleaseSpec spec = _specProvider.GetSpec(parentHeader.Number + 1);

            BlockHeader header = new (
                parentHeader.Hash,
                Keccak.OfAnEmptySequenceRlp,
                Address.Zero,
                1,
                parentBlock.Number + 1,
                _gasLimitCalculator.GetGasLimit(parentBlock.Header),
                timestamp > parentBlock.Timestamp ? timestamp : parentBlock.Timestamp + 1,
                Array.Empty<byte>());

            // If the block isn't a checkpoint, cast a random vote (good enough for now)
            long number = header.Number;
            // Assemble the voting snapshot to check which votes make sense
            Snapshot snapshot = _snapshotManager.GetOrCreateSnapshot(number - 1, parentHeader.Hash);
            bool isEpochBlock = (ulong)number % 30000 == 0;
            if (!isEpochBlock && _proposals.Any())
            {
                // Gather all the proposals that make sense voting on
                List<Address> addresses = new();
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
                    if (_proposals.TryGetValue(header.Beneficiary!, out bool proposal))
                    {
                        header.Nonce = proposal ? Clique.NonceAuthVote : Clique.NonceDropVote;
                    }
                }
            }

            // Set the correct difficulty
            header.BaseFeePerGas = BaseFeeCalculator.Calculate(parentHeader, _specProvider.GetSpec(header.Number));
            header.Difficulty = CalculateDifficulty(snapshot, _sealer.Address);
            header.TotalDifficulty = parentBlock.TotalDifficulty + header.Difficulty;
            if (_logger.IsDebug)
                _logger.Debug($"Setting total difficulty to {parentBlock.TotalDifficulty} + {header.Difficulty}.");

            // Set extra data
            int mainBytesLength = Clique.ExtraVanityLength + Clique.ExtraSealLength;
            int signerBytesLength = isEpochBlock ? 20 * snapshot.Signers.Count : 0;
            int extraDataLength = mainBytesLength + signerBytesLength;
            header.ExtraData = new byte[extraDataLength];
            header.Bloom = Bloom.Empty;

            byte[] clientName = Encoding.UTF8.GetBytes("Nethermind " + ClientVersion.Version);
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
            if (header.Timestamp < _timestamper.UnixTime.Seconds)
            {
                header.Timestamp = new UInt256(_timestamper.UnixTime.Seconds);
            }

            _stateProvider.StateRoot = parentHeader.StateRoot;
            
            IEnumerable<Transaction> selectedTxs = _txSource.GetTransactions(parentBlock.Header, header.GasLimit);
            Block block = new BlockToProduce(header, selectedTxs, Array.Empty<BlockHeader>());
            header.TxRoot = new TxTrie(block.Transactions).RootHash;
            block.Header.Author = _sealer.Address;
            return block;
        }

        private UInt256 CalculateDifficulty(Snapshot snapshot, Address signer)
        {
            if (_snapshotManager.IsInTurn(snapshot, snapshot.Number + 1, signer))
            {
                if (_logger.IsInfo) _logger.Info("Producing in turn block");
                return Clique.DifficultyInTurn;
            }

            if (_logger.IsInfo) _logger.Info("Producing out of turn block");
            return Clique.DifficultyNoTurn;
        }

        public void Dispose()
        {
            _cancellationTokenSource?.Dispose();
            _timer?.Dispose();
        }
    }
}
