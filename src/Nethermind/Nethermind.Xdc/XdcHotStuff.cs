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
        private readonly ISnapshotManager _snapshotManager;
        private readonly IQuorumCertificateManager _quorumCertificateManager;
        private readonly IVotesManager _votesManager;
        private readonly ISigner _signer;
        private readonly ITimeoutTimer _timeoutTimer;
        private readonly IProcessExitSource _processExit;
        private readonly ILogger _logger;

        private CancellationTokenSource? _cancellationTokenSource;
        private Task? _runTask;
        private DateTime _lastActivityTime;
        private readonly object _lockObject = new();

        public event EventHandler<BlockEventArgs>? BlockProduced;

        private static readonly PayloadAttributes DefaultPayloadAttributes = new PayloadAttributes();
        private ulong _highestSelfMinedRound;
        private ulong _highestVotedRound;

        public XdcHotStuff(
            IBlockTree blockTree,
            IXdcConsensusContext xdcContext,
            ISpecProvider specProvider,
            IBlockProducer blockBuilder,
            IEpochSwitchManager epochSwitchManager,
            ISnapshotManager snapshotManager,
            IQuorumCertificateManager quorumCertificateManager,
            IVotesManager votesManager,
            ISigner signer,
            ITimeoutTimer timeoutTimer,
            IProcessExitSource processExit,
            ILogManager logManager)
        {
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _xdcContext = xdcContext;
            _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
            _blockBuilder = blockBuilder ?? throw new ArgumentNullException(nameof(blockBuilder));
            _epochSwitchManager = epochSwitchManager ?? throw new ArgumentNullException(nameof(epochSwitchManager));
            _snapshotManager = snapshotManager;
            _quorumCertificateManager = quorumCertificateManager ?? throw new ArgumentNullException(nameof(quorumCertificateManager));
            _votesManager = votesManager ?? throw new ArgumentNullException(nameof(votesManager));
            _signer = signer ?? throw new ArgumentNullException(nameof(signer));
            _timeoutTimer = timeoutTimer;
            _processExit = processExit;
            _logger = logManager?.GetClassLogger<XdcHotStuff>() ?? throw new ArgumentNullException(nameof(logManager));

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
                await WaitForBlockTreeHead(_cancellationTokenSource!.Token);

                _blockTree.NewHeadBlock += OnNewHeadBlock;

                _xdcContext.NewRoundSetEvent += OnNewRound;

                // Initialize round from head
                InitializeRoundFromHead();

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
                _xdcContext.NewRoundSetEvent -= OnNewRound;
            }
        }

        private void OnNewRound(object sender, NewRoundEventArgs args)
        {
            XdcBlockHeader head = _blockTree.Head.Header as XdcBlockHeader
                ?? throw new InvalidOperationException("BlockTree head is not XdcBlockHeader.");
            //TODO Should this use be previous round ?
            IXdcReleaseSpec spec = _specProvider.GetXdcSpec(head, args.NewRound);

            //TODO Technically we have to apply timeout exponents from spec, but they are always 1
            _timeoutTimer.Reset(TimeSpan.FromSeconds(spec.TimeoutPeriod));

            TimeSpan roundDuration = DateTime.UtcNow - _xdcContext.RoundStarted;
            _logger.Info($"Round {args.NewRound} completed in {roundDuration.TotalSeconds:F2}s");
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

            ulong initialRound = xdcHead.ExtraConsensusData?.BlockRound + 1 ?? 0;
            _logger.Info($"Initialized round counter from head: round {initialRound}");
        }

        /// <summary>
        /// Main consensus flow
        /// </summary>
        private async Task MainFlow()
        {
            CancellationToken ct = _cancellationTokenSource!.Token;

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await RunRoundChecks(ct);
                    await Task.Delay(50, ct);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (InvalidOperationException ex)
                {
                    _logger.Error("", ex);
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.Error($"Error processing round {_xdcContext.CurrentRound}", ex);
                }
            }
        }

        /// <summary>
        /// Run checks for the current round: leader proposal, voting, timeout handling.
        /// </summary>
        internal async Task RunRoundChecks(CancellationToken ct)
        {
            ulong currentRound = _xdcContext.CurrentRound;

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

            // Get epoch info and check for epoch switch
            EpochSwitchInfo epochInfo = _epochSwitchManager.GetEpochSwitchInfo(parent);
            if (epochInfo?.Masternodes == null || epochInfo.Masternodes.Length == 0)
            {
                _logger.Warn($"Round {currentRound}: No masternodes in epoch, skipping");
                return;
            }

            bool isMyTurn = IsMyTurnAndTime(parent, currentRound, epochInfo.Masternodes, spec);
            _logger.Info($"Round {currentRound}: Leader={GetLeaderAddress(parent, currentRound, epochInfo.Masternodes, spec)}, MyTurn={isMyTurn}, Committee={epochInfo.Masternodes.Length} nodes");

            if (isMyTurn)
            {
                _highestSelfMinedRound = currentRound;
                Task blockBuilder = BuildAndProposeBlock(parent, currentRound, spec, ct);
            }

            await CommitCertificateAndVote(parent, epochInfo, currentRound);
        }

        private XdcBlockHeader GetParentForRound()
        {
            BlockHeader parent = _blockTree.FindHeader(_xdcContext.HighestQC?.ProposedBlockInfo.Hash, _xdcContext.HighestQC?.ProposedBlockInfo.BlockNumber);
            if (parent == null)
                parent = _blockTree.Head.Header;
            return parent as XdcBlockHeader;
        }

        /// <summary>
        /// Build block with parentQC, arm timeout, emit BlockProduced.
        /// </summary>
        private async Task BuildAndProposeBlock(XdcBlockHeader parent, ulong currentRound, IXdcReleaseSpec spec, CancellationToken ct)
        {
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
            catch (Exception ex)
            {
                _logger.Error($"Failed to build block in round {currentRound}", ex);
            }
        }

        /// <summary>
        /// Voter path - commit received QC, check voting rule, cast vote.
        /// </summary>
        private async Task CommitCertificateAndVote(XdcBlockHeader head, EpochSwitchInfo epochInfo, ulong currentRound)
        {
            if (head.ExtraConsensusData?.QuorumCert is null)
                throw new InvalidOperationException("Head block missing consensus data.");

            // Commit/record the header's QC
            _quorumCertificateManager.CommitCertificate(head.ExtraConsensusData.QuorumCert);

            if (_highestVotedRound >= _xdcContext.CurrentRound)
                return;

            _highestVotedRound = currentRound;

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
            if (headRound > _xdcContext.CurrentRound)
            {
                _logger.Warn($"New head block round is ahead of us.");
                //TODO This should probably trigger a sync
            }

            // Signal new round
            _lastActivityTime = DateTime.UtcNow;
        }

        /// <summary>
        /// Check if the current node is the leader for the given round.
        /// Uses epoch switch manager and spec to determine leader via round-robin rotation.
        /// </summary>
        private bool IsMyTurnAndTime(XdcBlockHeader parent, ulong round, Address[] masternodes, IXdcReleaseSpec spec)
        {
            if (_highestSelfMinedRound <= round)
            {
                //Already produced block for this round
                return false;
            }

            if ((long)parent.Timestamp + spec.MinePeriod > DateTimeOffset.UtcNow.ToUnixTimeSeconds())
            {
                //Not enough time has passed since last block
                return false;
            }

            if (parent.Hash != _xdcContext.HighestQC.ProposedBlockInfo.Hash)
            {
                //We have not reached QC vote threshold yet
                return false;
            }

            if (masternodes.Length == 0)
                return false;

            Address leaderAddress = GetLeaderAddress(parent, round, masternodes, spec);
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
                (masternodes, _) = _snapshotManager.CalculateNextEpochMasternodes(currentHead.Number, currentHead.ParentHash, spec);
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
    }
}
