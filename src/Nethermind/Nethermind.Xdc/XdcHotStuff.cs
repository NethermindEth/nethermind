// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using Nethermind.Xdc.Spec;
using Nethermind.Xdc.Types;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.Xdc
{
    /// <summary>
    /// This runner orchestrates the consensus loop: leader block proposal, voting, QC aggregation,
    /// timeout handling, and 3-chain finalization
    /// </summary>
    internal class XdcHotStuff(
        IBlockTree blockTree,
        IXdcConsensusContext xdcContext,
        ISpecProvider specProvider,
        IBlockProducer blockBuilder,
        IEpochSwitchManager epochSwitchManager,
        ISnapshotManager snapshotManager,
        IMasternodesCalculator masternodesCalculator,
        IQuorumCertificateManager quorumCertificateManager,
        IVotesManager votesManager,
        ISigner signer,
        ITimeoutTimer timeoutTimer,
        IProcessExitSource processExit,
        ISignTransactionManager signTransactionManager,
        ILogManager logManager) : IBlockProducerRunner
    {
        private readonly IBlockTree _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
        private readonly IXdcConsensusContext _xdcContext = xdcContext;
        private readonly ISpecProvider _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
        private readonly IBlockProducer _blockBuilder = blockBuilder ?? throw new ArgumentNullException(nameof(blockBuilder));
        private readonly IEpochSwitchManager _epochSwitchManager = epochSwitchManager ?? throw new ArgumentNullException(nameof(epochSwitchManager));
        private readonly ISnapshotManager _snapshotManager = snapshotManager ?? throw new ArgumentNullException(nameof(snapshotManager));
        private readonly IMasternodesCalculator _masternodesCalculator = masternodesCalculator ?? throw new ArgumentNullException(nameof(masternodesCalculator));
        private readonly IQuorumCertificateManager _quorumCertificateManager = quorumCertificateManager ?? throw new ArgumentNullException(nameof(quorumCertificateManager));
        private readonly IVotesManager _votesManager = votesManager ?? throw new ArgumentNullException(nameof(votesManager));
        private readonly ISigner _signer = signer ?? throw new ArgumentNullException(nameof(signer));
        private readonly ITimeoutTimer _timeoutTimer = timeoutTimer;
        private readonly IProcessExitSource _processExit = processExit;
        private readonly ILogger _logger = logManager?.GetClassLogger<XdcHotStuff>() ?? throw new ArgumentNullException(nameof(logManager));
        private readonly ISignTransactionManager _signTransactionManager = signTransactionManager ?? throw new ArgumentNullException(nameof(signTransactionManager));

        private CancellationTokenSource? _cancellationTokenSource;
        private Task? _runTask;
        private DateTime _lastActivityTime = DateTime.UtcNow;
        private readonly object _lockObject = new();

        public event EventHandler<BlockEventArgs>? BlockProduced;

        private ulong _highestSelfMinedRound;
        private ulong _highestVotedRound;
        private bool _writeRoundInfo = true;
        private long _highestSignTxNumber = 0;

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
                _logger.Info("XdcHotStuff consensus runner stopped");
            }
            catch (Exception ex)
            {
                _logger.Error("XdcHotStuff consensus runner encountered fatal error", ex);
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

            if (args.LastRoundDuration is { } lastRoundDuration)
                _logger.Info($"Round {args.PreviousRound} completed in {lastRoundDuration.TotalSeconds:F2}s");

            _writeRoundInfo = true;
        }

        /// <summary>
        /// Wait for blockTree.Head to become non-null during bootstrap.
        /// </summary>
        private async Task WaitForBlockTreeHead(CancellationToken cancellationToken)
        {
            _logger.Debug("Waiting for blockTree.Head to initialize...");
            while (_blockTree.Head == null)
            {
                await Task.Delay(100, cancellationToken);
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

            _quorumCertificateManager.Initialize(xdcHead);
            _logger.Info($"Initialized round {_xdcContext.CurrentRound} from head.");
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
                    if (!_blockTree.IsSyncing().isSyncing)
                    {
                        await RunRoundChecks(ct);
                    }
                    await Task.Delay(50, ct);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (InvalidOperationException)
                {
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

            XdcBlockHeader? roundParent = GetParentForRound();
            if (roundParent == null)
            {
                throw new InvalidOperationException($"Head is null or not XdcBlockHeader.");
            }

            // Get XDC spec for this round
            IXdcReleaseSpec spec = _specProvider.GetXdcSpec(roundParent, currentRound);

            // Get epoch info and check for epoch switch
            EpochSwitchInfo epochInfo = _epochSwitchManager.GetEpochSwitchInfo(roundParent);
            if (epochInfo?.Masternodes == null || epochInfo.Masternodes.Length == 0)
            {
                _logger.Warn($"Round {currentRound}: No masternodes in epoch, skipping");
                return;
            }

            if (spec.SwitchBlock < roundParent.Number)
            {
                await CommitCertificateAndVote(roundParent, epochInfo);
            }

            bool isMyTurn = IsMyTurn(roundParent, currentRound, spec);

            if (_writeRoundInfo)
                _logger.Info($"Round {currentRound}: Leader={GetLeaderAddress(roundParent, currentRound, spec)}, MyTurn={isMyTurn}, Committee={epochInfo.Masternodes.Length} nodes");

            if (isMyTurn && IsItTimeToPropose(roundParent, currentRound, spec))
            {
                _highestSelfMinedRound = currentRound;
                Task blockBuilder = BuildAndProposeBlock(roundParent, currentRound, spec, ct);
            }

            if (_highestSignTxNumber < roundParent.Number
                && ((roundParent.Number % spec.MergeSignRange == 0)))
            {
                Snapshot snapshot = _snapshotManager.GetSnapshotByBlockNumber(roundParent.Number, spec);
                if (snapshot is not null && snapshot.NextEpochCandidates.AsSpan().IndexOf(_signer.Address) != -1)
                {
                    _highestSignTxNumber = roundParent.Number;
                    await _signTransactionManager.SubmitTransactionSign(roundParent, spec);
                }
            }

            _writeRoundInfo = false;
        }

        private XdcBlockHeader GetParentForRound() => _blockTree.Head.Header as XdcBlockHeader;

        /// <summary>
        /// Build block with parentQC.
        /// </summary>
        internal async Task BuildAndProposeBlock(XdcBlockHeader parent, ulong currentRound, IXdcReleaseSpec spec, CancellationToken ct)
        {
            DateTime now = DateTime.UtcNow;

            try
            {
                QuorumCertificate highestQC = _xdcContext.HighestQC;
                XdcBlockHeader parentHeader = FindHeaderToBuildOn(highestQC) ?? parent;
                ulong parentTimestamp = parentHeader.Timestamp;
                ulong minTimestamp = parentTimestamp + (ulong)spec.MinePeriod;
                ulong currentTimestamp = (ulong)new DateTimeOffset(now).ToUnixTimeSeconds();

                _logger.Debug($"Round {currentRound}: Building proposal block");

                XdcPayloadAttributes payloadAttributes = new();
                payloadAttributes.Round = currentRound;
                payloadAttributes.QuorumCertificate = highestQC;

                if (currentTimestamp < minTimestamp)
                {
                    TimeSpan delay = TimeSpan.FromSeconds(minTimestamp - currentTimestamp);
                    _logger.Debug($"Round {currentRound}: Waiting {delay.TotalSeconds:F1}s for minimum mining time");
                    // Enforce minimum mining time per XDC rules
                    await Task.Delay(delay, ct);
                }

                payloadAttributes.Timestamp = (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

                Task<Block?> proposedBlockTask =
                    _blockBuilder.BuildBlock(parentHeader, null, payloadAttributes, IBlockProducer.Flags.None, ct);

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

            XdcBlockHeader FindHeaderToBuildOn(QuorumCertificate highestQC) =>
                _blockTree.FindHeader(
                    highestQC.ProposedBlockInfo.Hash,
                    highestQC.ProposedBlockInfo.BlockNumber) as XdcBlockHeader;
        }

        /// <summary>
        /// Voter path - commit received QC, check voting rule, cast vote.
        /// </summary>
        private async Task CommitCertificateAndVote(XdcBlockHeader head, EpochSwitchInfo epochInfo)
        {
            if (head.ExtraConsensusData?.QuorumCert is null)
                throw new InvalidOperationException("Head block missing consensus data.");

            ulong votingRound = head.ExtraConsensusData.BlockRound;
            if (_highestVotedRound >= votingRound)
                return;

            if (head.ExtraConsensusData.QuorumCert.Hash != _xdcContext.HighestQC?.Hash)
            {
                // Commit/record the header's QC
                _quorumCertificateManager.CommitCertificate(head.ExtraConsensusData.QuorumCert);
            }

            // Check if we are in the masternode set
            if (!IsMasternode(epochInfo, _signer.Address))
            {
                SetHighestVotedRound(votingRound);
                if (_logger.IsDebug)
                    _logger.Debug($"Round {votingRound}: Skipped voting (not in masternode set)");
                return;
            }

            // Check voting rule
            if (!head.IsSelfMined && !_votesManager.VerifyVotingRules(head, out string error))
            {
                if (_logger.IsDebug)
                    _logger.Debug($"Round {votingRound}: Voting rule not satisfied for block #{head.Number}, hash={head.Hash}: {error}");
                return;
            }

            try
            {
                BlockRoundInfo voteInfo = new(head.Hash!, head.ExtraConsensusData.BlockRound, head.Number);
                SetHighestVotedRound(votingRound);
                await _votesManager.CastVote(voteInfo);
                _lastActivityTime = DateTime.UtcNow;
                if (_logger.IsInfo)
                    _logger.Info($"Round {votingRound}: Voted for block #{head.Number}, hash={head.Hash}");
            }
            catch (Exception ex)
            {
                _logger.Error($"Round {votingRound}: Failed to cast vote.", ex);
            }

            void SetHighestVotedRound(ulong votingRound)
            {
                if (votingRound > _highestVotedRound)
                    _highestVotedRound = votingRound;
            }
        }

        /// <summary>
        /// Handler for blockTree.NewHeadBlock event - signals new round on head changes.
        /// </summary>
        private void OnNewHeadBlock(object? sender, BlockEventArgs e)
        {
            if (e.Block.Header is not XdcBlockHeader xdcHead)
                throw new InvalidOperationException($"Expected an XDC header, but got {e.Block.Header.GetType().FullName}");

            if (_logger.IsInfo)
                _logger.Info($"New head block #{xdcHead.Number}, round={xdcHead.ExtraConsensusData?.BlockRound}, hash={xdcHead.Hash}, our round={_xdcContext.CurrentRound}");

            if (xdcHead.ExtraConsensusData is null)
                throw new InvalidOperationException("New head block missing ExtraConsensusData");

            // Signal new round
            _lastActivityTime = DateTime.UtcNow;
        }

        /// <summary>
        /// Check if it's time to propose a block for the given round.
        /// </summary>
        private bool IsItTimeToPropose(XdcBlockHeader parent, ulong round, IXdcReleaseSpec spec)
        {
            if (_highestSelfMinedRound >= round)
            {
                //Already produced block for this round
                return false;
            }

            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if ((long)parent.Timestamp + spec.MinePeriod > now)
            {
                //Not enough time has passed since last block
                return false;
            }

            int fallbackPeriod = spec.TimeoutPeriod / 2;
            if ((long)parent.Timestamp + fallbackPeriod < now)
            {
                // If we are too far into the mining period, we will not wait for QC voting to finish and proceed with whatever is highest
                return true;
            }

            if (parent.Hash != _xdcContext.HighestQC.ProposedBlockInfo.Hash)
            {
                //We have not reached QC vote threshold yet
                return false;
            }

            return true;
        }

        /// <summary>
        /// Check if the current node is the leader for the given round.
        /// Uses epoch switch manager and spec to determine leader via round-robin rotation.
        /// </summary>
        private bool IsMyTurn(XdcBlockHeader parent, ulong round, IXdcReleaseSpec spec)
        {
            Address leaderAddress = GetLeaderAddress(parent, round, spec);
            return leaderAddress == _signer.Address;
        }

        /// <summary>
        /// Get the leader address for a given round using round-robin rotation.
        /// Leader selection: (round % epochLength) % masternodeCount
        /// </summary>
        public Address GetLeaderAddress(XdcBlockHeader currentHead, ulong round, IXdcReleaseSpec spec)
        {
            Address[] currentMasternodes;
            if (_epochSwitchManager.IsEpochSwitchAtRound(round, currentHead))
            {
                //TODO calculate master nodes based on the current round
                (currentMasternodes, _) = _masternodesCalculator.CalculateNextEpochMasternodes(currentHead.Number + 1, currentHead.Hash, spec);
            }
            else
            {
                EpochSwitchInfo epochSwitchInfo = _epochSwitchManager.GetEpochSwitchInfo(currentHead);
                currentMasternodes = epochSwitchInfo.Masternodes;
            }

            int currentLeaderIndex = ((int)round % spec.EpochLength % currentMasternodes.Length);
            return currentMasternodes[currentLeaderIndex];
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
            CancellationTokenSource? cts;

            lock (_lockObject)
            {
                if (_cancellationTokenSource == null)
                {
                    return;
                }

                cts = _cancellationTokenSource;
                task = _runTask;
                _cancellationTokenSource = null;
                _runTask = null;
            }

            _logger.Debug("Stopping XdcHotStuff consensus runner...");

            cts.Cancel();

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

            cts.Dispose();
            _logger.Info("XdcHotStuff consensus runner stopped");
        }

        private static bool IsMasternode(EpochSwitchInfo epochInfo, Address node) =>
            epochInfo.Masternodes.AsSpan().IndexOf(node) != -1;
    }
}
