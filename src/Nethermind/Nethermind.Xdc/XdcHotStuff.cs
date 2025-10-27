// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using Nethermind.Xdc.Spec;
using Nethermind.Xdc.Types;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Nethermind.Xdc
{
    /// <summary>
    /// This runner orchestrates the consensus loop: leader block proposal, voting, QC aggregation,
    /// timeout handling, and 3-chain finalization
    /// </summary>
    internal class XdcHotStuff : IBlockProducerRunner
    {
        private readonly IBlockTree _blockTree;
        private readonly IXdcConsensusContext _xdcContext;
        private readonly ISpecProvider _specProvider;
        private readonly IBlockProducer _blockBuilder;
        private readonly IEpochSwitchManager _epochSwitchManager;
        private readonly IQuorumCertificateManager _quorumCertificateManager;
        private readonly ITimeoutCertificateManager _timeoutCertificateManager;
        private readonly IVotesManager _votesManager;
        private readonly ISigner _signer;
        private readonly IProcessExitSource _processExit;
        private readonly ILogger _logger;

        private readonly Channel<RoundSignal> _newRoundSignals;
        private CancellationTokenSource? _cancellationTokenSource;
        private Task? _runTask;
        private RoundCount _roundCount;
        private DateTime _lastActivityTime;
        private readonly object _lockObject = new();

        public event EventHandler<BlockEventArgs>? BlockProduced;

        private static readonly PayloadAttributes DefaultPayloadAttributes = new PayloadAttributes();

        public XdcHotStuff(
            IBlockTree blockTree,
            IXdcConsensusContext xdcContext,
            ISpecProvider specProvider,
            IBlockProducer blockBuilder,
            IEpochSwitchManager epochSwitchManager,
            IQuorumCertificateManager quorumCertificateManager,
            ITimeoutCertificateManager timeoutCertificateManager,
            IVotesManager votesManager,
            ISigner signer,
            IProcessExitSource processExit,
            ILogManager logManager)
        {
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _xdcContext = xdcContext;
            _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
            _blockBuilder = blockBuilder ?? throw new ArgumentNullException(nameof(blockBuilder));
            _epochSwitchManager = epochSwitchManager ?? throw new ArgumentNullException(nameof(epochSwitchManager));
            _quorumCertificateManager = quorumCertificateManager ?? throw new ArgumentNullException(nameof(quorumCertificateManager));
            _timeoutCertificateManager = timeoutCertificateManager ?? throw new ArgumentNullException(nameof(timeoutCertificateManager));
            _votesManager = votesManager ?? throw new ArgumentNullException(nameof(votesManager));
            _signer = signer ?? throw new ArgumentNullException(nameof(signer));
            _processExit = processExit;
            _logger = logManager?.GetClassLogger<XdcHotStuff>() ?? throw new ArgumentNullException(nameof(logManager));

            _newRoundSignals = Channel.CreateUnbounded<RoundSignal>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false
            });

            _roundCount = new RoundCount(0);
            _lastActivityTime = DateTime.UtcNow;
        }

        /// <summary>
        /// Starts the consensus runner.
        /// </summary>
        public void Start()
        {
            lock (_lockObject)
            {
                if (_cancellationTokenSource != null)
                {
                    _logger.Info("XdcHotStuff already started, ignoring duplicate Start() call");
                    return;
                }

                _cancellationTokenSource = new CancellationTokenSource();

                _processExit.Token.Register(() =>
                {
                    _logger.Info("Process exit detected, stopping XdcHotStuff consensus runner...");
                    _cancellationTokenSource?.Cancel();
                });

                _runTask = Run();
                _logger.Info("XdcHotStuff consensus runner started");
            }
        }

        /// <summary>
        /// Main execution loop - waits for blockchain initialization then starts consensus.
        /// </summary>
        private async Task Run()
        {
            try
            {
                // Bootstrap: wait for initial head
                await WaitForBlockTreeHead(_cancellationTokenSource!.Token);

                // Subscribe to new head notifications
                _blockTree.NewHeadBlock += OnNewHeadBlock;

                _xdcContext.NewRoundSetEvent += OnNewRoundSetEvent;

                // Initialize round from head
                InitializeRoundFromHead();

                // Trigger initial round
                await _newRoundSignals.Writer.WriteAsync(new RoundSignal((XdcBlockHeader)_blockTree.Head.Header), _cancellationTokenSource.Token);

                // Main consensus flow
                await MainFlow();
            }
            catch (OperationCanceledException)
            {
                _logger.Info("XdcHotStuff consensus runner stopped gracefully");
            }
            catch (Exception ex)
            {
                _logger.Error("XdcHotStuff consensus runner encountered unexpected error", ex);
                throw;
            }
            finally
            {
                _blockTree.NewHeadBlock -= OnNewHeadBlock;
                _xdcContext.NewRoundSetEvent -= OnNewRoundSetEvent;
            }
        }

        private void OnNewRoundSetEvent(ulong round)
        {
            
        }

        /// <summary>
        /// Wait for blockTree.Head to become non-null during bootstrap.
        /// </summary>
        private async Task WaitForBlockTreeHead(CancellationToken cancellationToken)
        {
            _logger.Debug("Waiting for blockTree.Head to initialize...");
            while (_blockTree.Head == null)
            {
                await Task.Delay(200, cancellationToken);
            }
            _logger.Debug($"BlockTree initialized with head at block #{_blockTree.Head.Number}");
        }

        /// <summary>
        /// Initialize RoundCount from the current head's ExtraConsensusData.BlockRound.
        /// </summary>
        private void InitializeRoundFromHead()
        {
            if (_blockTree.Head.Header is not XdcBlockHeader xdcHead)
                throw new InvalidBlockException(_blockTree.Head, "Head is not XdcBlockHeader.");

            ulong initialRound = xdcHead.ExtraConsensusData?.BlockRound ?? 0;
            _roundCount = new RoundCount(initialRound);
            _logger.Info($"Initialized round counter from head: round {initialRound}");
        }

        /// <summary>
        /// Main consensus flow
        /// </summary>
        private async Task MainFlow()
        {            
            CancellationToken ct = _cancellationTokenSource!.Token;

            await foreach (RoundSignal signal in _newRoundSignals.Reader.ReadAllAsync(ct))
            {
                try
                {
                    await ProcessRound(ct);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.Error($"Error processing round {_roundCount.Current}", ex);
                }
            }
        }

        /// <summary>
        /// Process a single round
        /// </summary>
        private async Task ProcessRound(CancellationToken ct)
        {
            ulong currentRound = _roundCount.Current;

            XdcBlockHeader? parent = GetParentForRound();
            if (parent == null)
            {
                throw new InvalidOperationException($"Head is null or not XdcBlockHeader.");
            }

            // Get XDC spec for this round
            IXdcReleaseSpec spec = _specProvider.GetXdcSpec(parent, currentRound);
            if (spec == null)
            {
                _logger.Error($"Round {currentRound}: Failed to get XDC spec, skipping");
                return;
            }

            //TODO Technically we have to apply timeout exponents from spec, but they are always 1
            DateTimeOffset timeToTimeout = DateTimeOffset.FromUnixTimeSeconds((long)parent.Timestamp + spec.TimeoutPeriod);

            TimeSpan timeout = DateTimeOffset.UtcNow - timeToTimeout;
            using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);

            // Get epoch info and check for epoch switch
            EpochSwitchInfo epochInfo = _epochSwitchManager.GetEpochSwitchInfo(parent);
            if (epochInfo?.Masternodes == null || epochInfo.Masternodes.Length == 0)
            {
                _logger.Warn($"Round {currentRound}: No masternodes in epoch, skipping");
                return;
            }

            bool isMyTurn = IsMyTurn(parent, currentRound, epochInfo.Masternodes, spec);
            Address? myAddress = _signer.Address;

            _logger.Info($"Round {currentRound}: Leader={GetLeaderAddress(parent, currentRound, epochInfo.Masternodes, spec)}, MyTurn={isMyTurn}, Committee={epochInfo.Masternodes.Length} nodes");

            if (isMyTurn)
            {
                Task blockBuilder = BuildAndProposeBlock(parent, spec, timeoutCts.Token);
            }

            await ExecuteVoterPath(parent, epochInfo);

            try
            {
                // Race between: new round signal OR timeout
                Task taskDelay = Task.Delay(timeout, timeoutCts.Token);
                var completedTask = await Task.WhenAny(
                    WaitForNextRoundOrQC(timeoutCts.Token),
                    taskDelay
                );

                if (completedTask == taskDelay)
                {
                    // Timeout occurred - trigger timeout protocol
                    await HandleTimeout(currentRound, ct);
                }
            }
            catch (OperationCanceledException) when (timeoutCts.Token.IsCancellationRequested)
            {
                // Round timeout - handle it
                await HandleTimeout(currentRound, ct);
            }

            TimeSpan roundDuration = DateTime.UtcNow - _roundCount.RoundStarted;
            _logger.Info($"Round {currentRound} completed in {roundDuration.TotalSeconds:F2}s");
        }


        private async Task WaitForNextRoundOrQC(CancellationToken ct)
        {
            // This returns when _newRoundSignals receives next signal
            // OR when _xdcContext.NewRoundSetEvent fires
            // Essentially waiting for the leader to form a QC and advance the round
            await Task.Delay(-1, ct); // Wait indefinitely until cancelled or round advances
        }

        private XdcBlockHeader GetParentForRound()
        {
            //TODO this comes from XDC repo?
            BlockHeader parent = _blockTree.FindHeader(_xdcContext.HighestQC.ProposedBlockInfo.Hash, _xdcContext.HighestQC.ProposedBlockInfo.BlockNumber);
            if (parent == null)
                parent = _blockTree.Head.Header;
            return parent as XdcBlockHeader;
        }

        /// <summary>
        /// Build block with parentQC, arm timeout, emit BlockProduced.
        /// </summary>
        private async Task BuildAndProposeBlock(XdcBlockHeader parent, IXdcReleaseSpec spec, CancellationToken ct)
        {
            ulong currentRound = _roundCount.Current;
            DateTime now = DateTime.UtcNow;

            try
            {
                ulong parentTimestamp = parent.Timestamp;
                ulong minTimestamp = parentTimestamp + (ulong)spec.MinePeriod;
                ulong currentTimestamp = (ulong)new DateTimeOffset(now).ToUnixTimeSeconds();

                _logger.Debug($"Round {currentRound}: Building proposal block");

                DefaultPayloadAttributes.Timestamp = minTimestamp;

                if (currentTimestamp < minTimestamp)
                {
                    TimeSpan delay = TimeSpan.FromSeconds(minTimestamp - currentTimestamp);
                    _logger.Debug($"Round {currentRound}: Waiting {delay.TotalSeconds:F1}s for minimum mining time");
                    // Enforce minimum mining time per XDC rules
                    // TODO we should probably wait for the vote threshold to be reached here
                    await Task.Delay(delay, ct);
                }

                Task<Block?> proposedBlockTask =
                    _blockBuilder.BuildBlock(parent, null, DefaultPayloadAttributes, IBlockProducer.Flags.None, ct);

                Block? proposedBlock = await proposedBlockTask;

                if (proposedBlock != null)
                {
                    _lastActivityTime = DateTime.UtcNow;
                    _logger.Info($"Round {currentRound}: Block #{proposedBlock.Number} built successfully, hash={proposedBlock.Hash}");

                    // This will trigger broadcasting the block via P2P
                    BlockProduced?.Invoke(this, new BlockEventArgs(proposedBlock));
                }
                else
                {
                    _logger.Warn($"Round {currentRound}: Block builder returned null");
                }
            }
            catch (OperationCanceledException)
            {
                _logger.Warn($"Failed to build block in round {currentRound}: Timeout after {(DateTime.UtcNow - now).TotalSeconds}s");
            }
        }

        /// <summary>
        /// Voter path - commit received QC, check voting rule, cast vote.
        /// </summary>
        private async Task ExecuteVoterPath(XdcBlockHeader head, EpochSwitchInfo epochInfo)
        {
            ulong currentRound = _roundCount.Current;

            // Commit/record the header's QC
            if (head.ExtraConsensusData?.QuorumCert != null)
            {
                try
                {
                    _quorumCertificateManager.CommitCertificate(head.ExtraConsensusData.QuorumCert);
                    _logger.Debug($"Round {currentRound}: Committed QC from head block #{head.Number}");
                }
                catch (Exception ex)
                {
                    _logger.Error($"Round {currentRound}: Failed to commit QC", ex);
                }
            }

            // Check if we are in the masternode set

            if (!Array.Exists(epochInfo.Masternodes, (a) => a == _signer.Address))
            {
                _logger.Info($"Round {currentRound}: Skipped voting (not in masternode set)");
                return;
            }

            // Check voting rule 
            bool canVote = _votesManager.VerifyVotingRules(head);
            if (!canVote)
            {
                _logger.Info($"Round {currentRound}: Voting rule not satisfied for block #{head.Hash}");
                return;
            }

            try
            {
                BlockRoundInfo voteInfo = new BlockRoundInfo(head.Hash!, head.ExtraConsensusData.BlockRound, head.Number);
                await _votesManager.CastVote(voteInfo);
                _lastActivityTime = DateTime.UtcNow;
                _logger.Info($"Round {currentRound}: Voted for block #{head.Number}, hash={head.Hash}");
            }
            catch (Exception ex)
            {
                _logger.Error($"Round {currentRound}: Failed to cast vote.", ex);
            }
        }

        /// <summary>
        /// Timeout handling - broadcast timeout with highQC, wait for TC.
        /// </summary>
        private async Task HandleTimeout(ulong round, CancellationToken ct)
        {
            try
            {
                // Get current highQC from QC manager
                QuorumCertificate highQC = _xdcContext.HighestQC;

                // Broadcast timeout via TC manager 
                _timeoutCertificateManager.OnCountdownTimer();
                _logger.Info($"Round {round}: Broadcasted timeout with highQC round={highQC?.ProposedBlockInfo.Round ?? 0}");

                // In a real implementation, the TC manager would notify us when TC is formed
                // For now, we advance round optimistically after timeout
                // (In production, this would be triggered by TC manager callback/event)
                await Task.Delay(1000, ct); // Brief wait for TC aggregation

                // Advance round after TC
                StartNewRound();
            }
            catch (Exception ex)
            {
                _logger.Error($"Round {round}: Error handling timeout: {ex.Message}");
            }
        }

        /// <summary>
        /// Handler for blockTree.NewHeadBlock event - signals new round on head changes.
        /// </summary>
        private void OnNewHeadBlock(object? sender, BlockEventArgs e)
        {
            if (e.Block.Header is not XdcBlockHeader xdcHead)
                throw new InvalidOperationException($"Expected an XDC header, but got {e.Block.Header.GetType().FullName}");

            _logger.Debug($"New head block #{xdcHead.Number}, round={xdcHead.ExtraConsensusData?.BlockRound}");

            if (xdcHead.ExtraConsensusData is null)
                throw new InvalidOperationException("New head block missing ExtraConsensusData");

            ulong headRound = xdcHead.ExtraConsensusData.BlockRound;
            if (headRound > _roundCount.Current)
            {
                _logger.Warn($"New head block round is ahead of us. Advancing to round {headRound + 1}");
                //TODO This should probably trigger a sync
            }

            _roundCount = new RoundCount(headRound + 1);
            // Signal new round
            _lastActivityTime = DateTime.UtcNow;
            _newRoundSignals.Writer.TryWrite(new RoundSignal(xdcHead));
        }

        /// <summary>
        /// Start a new round: increment counter and signal the channel.
        /// </summary>
        private void StartNewRound()
        {
            ulong previousRound = _roundCount.Current;
            _roundCount = new RoundCount(_roundCount.Current + 1);
            _logger.Info($"Transitioning from round {previousRound} to round {_roundCount.Current}");
        }

        /// <summary>
        /// Check if the current node is the leader for the given round.
        /// Uses epoch switch manager and spec to determine leader via round-robin rotation.
        /// </summary>
        private bool IsMyTurn(XdcBlockHeader xdcHead, ulong round, Address[] masternodes, IXdcReleaseSpec spec)
        {
            if (masternodes.Length == 0)
                return false;

            Address leaderAddress = GetLeaderAddress(xdcHead, round, masternodes, spec);
            return leaderAddress == _signer.Address;
        }

        /// <summary>
        /// Get the leader address for a given round using round-robin rotation.
        /// Leader selection: (round % epochLength) % masternodeCount
        /// </summary>
        private Address GetLeaderAddress(XdcBlockHeader currentHead, ulong round, Address[] masternodes, IXdcReleaseSpec spec)
        {
            if (masternodes.Length == 0)
            {
                throw new InvalidOperationException("Cannot determine leader with empty masternode set");
            }

            EpochSwitchInfo epochSwitchInfo = null;
            if (_epochSwitchManager.IsEpochSwitchAtRound(round, currentHead))
            {
                //TODO calculate master nodes based on the current round
            }
            else
            {
                epochSwitchInfo = _epochSwitchManager.GetEpochSwitchInfo(currentHead);
            }

            int currentLeaderIndex = ((int)round % spec.EpochLength % epochSwitchInfo.Masternodes.Length);
            return masternodes[currentLeaderIndex];
        }

        /// <summary>
        /// Check if the runner is actively producing blocks within the given interval.
        /// </summary>
        public bool IsProducingBlocks(ulong? maxProducingInterval)
        {
            if (!maxProducingInterval.HasValue)
            {
                return _cancellationTokenSource != null && !_cancellationTokenSource.IsCancellationRequested;
            }

            TimeSpan elapsed = DateTime.UtcNow - _lastActivityTime;
            TimeSpan maxInterval = TimeSpan.FromSeconds(maxProducingInterval.Value);

            return elapsed <= maxInterval;
        }

        /// <summary>
        /// Stop the consensus runner gracefully.
        /// </summary>
        public async Task StopAsync()
        {
            Task? task;

            lock (_lockObject)
            {
                if (_cancellationTokenSource == null)
                {
                    return;
                }

                task = _runTask;
                _cancellationTokenSource = null;
                _runTask = null;
            }

            _logger.Debug("Stopping XdcHotStuff consensus runner...");

            // Signal cancellation
            _cancellationTokenSource?.Cancel();

            _newRoundSignals.Writer.Complete();

            // Wait for task completion
            if (task != null)
            {
                try
                {
                    await task;
                }
                catch (OperationCanceledException)
                {
                    // Expected
                }
            }

            _cancellationTokenSource?.Dispose();
            _logger.Info("XdcHotStuff consensus runner stopped");
        }

        /// <summary>
        /// Internal signal type for round transitions.
        /// </summary>
        internal class RoundSignal
        {
            public XdcBlockHeader HeadBlockHeader { get; }

            public RoundSignal(XdcBlockHeader xdcBlockHeader)
            {
                HeadBlockHeader = xdcBlockHeader;
            }
        }

        /// <summary>
        /// Internal round counter wrapper.
        /// </summary>
        internal class RoundCount
        {
            public ulong Current { get; private set; }
            public DateTime RoundStarted { get; private set; }

            public RoundCount(ulong round)
            {
                Current = round;
            }

            public void SetRound(ulong round)
            {
                Current = round;
                RoundStarted = DateTime.UtcNow;
            }
        }
    }
}
