// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Consensus.Producers;
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
    internal class XdcHotStuff : IBlockProducerRunner
    {
        private readonly IBlockTree _blockTree;
        private readonly IXdcConsensusContext _xdcContext;
        private readonly ISpecProvider _specProvider;
        private readonly IBlockProducer _blockBuilder;
        private readonly IEpochSwitchManager _epochSwitchManager;
        private readonly IMasternodesCalculator _masternodesCalculator;
        private readonly IQuorumCertificateManager _quorumCertificateManager;
        private readonly IVotesManager _votesManager;
        private readonly ISigner _signer;
        private readonly ITimeoutTimer _timeoutTimer;
        private readonly IProcessExitSource _processExit;
        private readonly ILogger _logger;
        private readonly ISignTransactionManager _signTransactionManager;
        private readonly ITimeoutCertificateManager _timeoutCertificateManager;
        private readonly ISyncInfoManager _syncInfoManager;
        private readonly ConsensusEventChannel _channel;

        private CancellationTokenSource? _cancellationTokenSource;
        private Task? _runTask;
        private DateTime _lastActivityTime;
        private readonly object _lockObject = new();

        public event EventHandler<BlockEventArgs>? BlockProduced;

        private static readonly PayloadAttributes DefaultPayloadAttributes = new PayloadAttributes();
        private ulong _highestSelfMinedRound;
        private ulong _highestVotedRound;
        private long _highestSignTxNumber = 0;


        public XdcHotStuff(
            IBlockTree blockTree,
            IXdcConsensusContext xdcContext,
            ISpecProvider specProvider,
            IBlockProducer blockBuilder,
            IEpochSwitchManager epochSwitchManager,
            IMasternodesCalculator masternodesCalculator,
            IQuorumCertificateManager quorumCertificateManager,
            IVotesManager votesManager,
            ISigner signer,
            ITimeoutTimer timeoutTimer,
            IProcessExitSource processExit,
            ISignTransactionManager signTransactionManager,
            ITimeoutCertificateManager timeoutCertificateManager,
            ISyncInfoManager syncInfoManager,
            ConsensusEventChannel channel,
            ILogManager logManager)
        {
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _xdcContext = xdcContext;
            _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
            _blockBuilder = blockBuilder ?? throw new ArgumentNullException(nameof(blockBuilder));
            _epochSwitchManager = epochSwitchManager ?? throw new ArgumentNullException(nameof(epochSwitchManager));
            _masternodesCalculator = masternodesCalculator ?? throw new ArgumentNullException(nameof(masternodesCalculator));
            _quorumCertificateManager = quorumCertificateManager ?? throw new ArgumentNullException(nameof(quorumCertificateManager));
            _votesManager = votesManager ?? throw new ArgumentNullException(nameof(votesManager));
            _signer = signer ?? throw new ArgumentNullException(nameof(signer));
            _signTransactionManager = signTransactionManager ?? throw new ArgumentNullException(nameof(signTransactionManager));
            _timeoutCertificateManager = timeoutCertificateManager ?? throw new ArgumentNullException(nameof(timeoutCertificateManager));
            _syncInfoManager = syncInfoManager ?? throw new ArgumentNullException(nameof(syncInfoManager));
            _timeoutTimer = timeoutTimer;
            _processExit = processExit;
            _channel = channel;
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
                    if (_logger.IsWarn) _logger.Warn("XdcHotStuff already started.");
                    return;
                }

                _cancellationTokenSource = new CancellationTokenSource();

                _processExit.Token.Register(() =>
                {
                    if (_logger.IsInfo) _logger.Info("Process exit detected, stopping consensus runner.");
                    _cancellationTokenSource?.Cancel();
                });

                _runTask = Run();
                if (_logger.IsInfo) _logger.Info("XdcHotStuff consensus runner started.");
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

                await MainFlow();
            }
            catch (OperationCanceledException)
            {
                if (_logger.IsInfo) _logger.Info("XdcHotStuff consensus runner stopped.");
            }
            catch (Exception ex)
            {
                _logger.Error("XdcHotStuff consensus runner encountered a fatal error.", ex);
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

            if (_logger.IsDebug) _logger.Debug($"Round {args.PreviousRound} completed in {args.LastRoundDuration.TotalSeconds:F2}s.");

            TryStartProposal();
        }

        /// <summary>
        /// Wait for blockTree.Head to become non-null during bootstrap.
        /// </summary>
        private async Task WaitForBlockTreeHead(CancellationToken cancellationToken)
        {
            if (_logger.IsDebug) _logger.Debug("Waiting for block tree head to initialize...");
            while (_blockTree.Head == null)
            {
                await Task.Delay(100, cancellationToken);
            }
            if (_logger.IsDebug) _logger.Debug($"Block tree initialized at block #{_blockTree.Head.Number}.");
        }

        /// <summary>
        /// Initialize RoundCount from the current head's ExtraConsensusData.BlockRound.
        /// </summary>
        private void InitializeRoundFromHead()
        {
            if (_blockTree.Head.Header is not XdcBlockHeader xdcHead)
                throw new InvalidBlockException(_blockTree.Head, "Head is not XdcBlockHeader.");

            _quorumCertificateManager.Initialize(xdcHead);
            if (_logger.IsInfo) _logger.Info($"Initialized at round {_xdcContext.CurrentRound} from head block #{_blockTree.Head.Number}.");
        }

        private async Task MainFlow()
        {
            CancellationToken ct = _cancellationTokenSource!.Token;

            await foreach (IConsensusEvent e in _channel.Reader.ReadAllAsync(ct))
            {
                try
                {
                    switch (e)
                    {
                        case NewHeadEvent ev:             await HandleNewHead(ev); break;
                        case VoteReceivedEvent ev:         await HandleVote(ev); break;
                        case TimeoutVoteReceivedEvent ev:  await HandleTimeoutVote(ev); break;
                        case SyncInfoReceivedEvent ev:     HandleSyncInfo(ev); break;
                        case TimeoutElapsedEvent:          HandleTimeoutElapsed(); break;
                    }
                }
                catch (Exception ex)
                {
                    _logger.Error($"Unhandled error processing {e.GetType().Name} in round {_xdcContext.CurrentRound}.", ex);
                }
            }
        }


        private async Task HandleNewHead(NewHeadEvent e)
        {
            XdcBlockHeader head = e.Head;
            IXdcReleaseSpec spec = _specProvider.GetXdcSpec(head, _xdcContext.CurrentRound);
            if (spec == null) return;

            EpochSwitchInfo epochInfo = _epochSwitchManager.GetEpochSwitchInfo(head);
            if (epochInfo?.Masternodes == null || epochInfo.Masternodes.Length == 0) return;

            if (spec.SwitchBlock >= head.Number)
                return;

            await CommitCertificateAndVote(head, epochInfo);

            if (_highestSignTxNumber < head.Number
                && IsMasternode(epochInfo, _signer.Address)
                && (head.Number % spec.MergeSignRange == 0))
            {
                _highestSignTxNumber = head.Number;
                await _signTransactionManager.SubmitTransactionSign(head, spec);
            }
        }
        private async Task HandleVote(VoteReceivedEvent e)
        {
            await _votesManager.OnReceiveVote(e.Vote);
        }
        private async Task HandleTimeoutVote(TimeoutVoteReceivedEvent e)
        {
            await _timeoutCertificateManager.OnReceiveTimeout(e.Timeout);
        }
        private void HandleSyncInfo(SyncInfoReceivedEvent e)
        {
            _syncInfoManager.ProcessSyncInfo(e.Info);
        }
        private void HandleTimeoutElapsed()
        {
            _timeoutCertificateManager.OnCountdownTimer();
        }
        private void TryStartProposal()
        {
            ulong currentRound = _xdcContext.CurrentRound;
            XdcBlockHeader? roundParent = GetParentForRound();
            if (roundParent == null) return;

            IXdcReleaseSpec spec
                = _specProvider.GetXdcSpec(roundParent, currentRound);
            if (spec == null) return;

            bool isMyTurn = IsMyTurn(roundParent, currentRound, spec);
            if (_logger.IsDebug)
            {
                Address leader = GetLeaderAddress(roundParent, currentRound, spec);
                _logger.Debug(isMyTurn
                    ? $"Our turn to propose in round {currentRound}."
                    : $"Not our turn in round {currentRound} - leader is {leader}.");
            }

            if (isMyTurn && _highestSelfMinedRound < currentRound)
            {
                _highestSelfMinedRound = currentRound;
                _ = BuildAndProposeBlock(roundParent, currentRound, spec, _cancellationTokenSource!.Token);
            }
        }


        private XdcBlockHeader GetParentForRound()
        {
            return _blockTree.Head.Header as XdcBlockHeader;
        }

        /// <summary>
        /// Build block with parentQC.
        /// </summary>
        internal async Task BuildAndProposeBlock(XdcBlockHeader parent, ulong currentRound, IXdcReleaseSpec spec, CancellationToken ct)
        {
            DateTime now = DateTime.UtcNow;

            try
            {
                XdcBlockHeader parentHeader = FindHeaderToBuildOn(_xdcContext.HighestQC) ?? parent;
                ulong parentTimestamp = parentHeader.Timestamp;
                ulong minTimestamp = parentTimestamp + (ulong)spec.MinePeriod;
                ulong currentTimestamp = (ulong)new DateTimeOffset(now).ToUnixTimeSeconds();

                if (_logger.IsDebug) _logger.Debug($"Building proposal block for round {currentRound}.");

                DefaultPayloadAttributes.Timestamp = minTimestamp;

                if (currentTimestamp < minTimestamp)
                {
                    TimeSpan delay = TimeSpan.FromSeconds(minTimestamp - currentTimestamp);
                    if (_logger.IsDebug) _logger.Debug($"Waiting {delay.TotalSeconds:F1}s for minimum mining period in round {currentRound}.");
                    // Enforce minimum mining time per XDC rules
                    await Task.Delay(delay, ct);
                }

                Task<Block?> proposedBlockTask =
                    _blockBuilder.BuildBlock(parentHeader, null, DefaultPayloadAttributes, IBlockProducer.Flags.None, ct);

                Block? proposedBlock = await proposedBlockTask;

                if (proposedBlock != null)
                {
                    _lastActivityTime = DateTime.UtcNow;
                    // This will trigger broadcasting the block via P2P
                    BlockProduced?.Invoke(this, new BlockEventArgs(proposedBlock));
                }
                else
                {
                    if (_logger.IsWarn) _logger.Warn($"Block builder returned null in round {currentRound}.");
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to build block in round {currentRound}.", ex);
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
            var votingRound = head.ExtraConsensusData.BlockRound;
            if (_highestVotedRound >= votingRound)
                return;

            // Commit/record the header's QC
            _quorumCertificateManager.CommitCertificate(head.ExtraConsensusData.QuorumCert);

            // Check if we are in the masternode set
            if (!IsMasternode(epochInfo, _signer.Address))
            {
                if (_logger.IsDebug) _logger.Debug($"Skipping vote for round {votingRound} - not in masternode set.");
                return;
            }

            // Check voting rule
            bool canVote = _votesManager.VerifyVotingRules(head);
            if (!canVote)
            {
                if (_logger.IsDebug) _logger.Debug($"Voting rule not satisfied for block #{head.Number} in round {votingRound}.");
                return;
            }

            try
            {
                BlockRoundInfo voteInfo = new BlockRoundInfo(head.Hash!, head.ExtraConsensusData.BlockRound, head.Number);
                _highestVotedRound = votingRound;
                await _votesManager.CastVote(voteInfo);
                _lastActivityTime = DateTime.UtcNow;
                if (_logger.IsInfo) _logger.Info($"Voted for block #{head.Number} in round {votingRound} ({head.Hash}).");
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to cast vote in round {votingRound}.", ex);
            }
        }

        /// <summary>
        /// Handler for blockTree.NewHeadBlock event - signals new round on head changes.
        /// </summary>
        private void OnNewHeadBlock(object? sender, BlockEventArgs e)
        {
            if (e.Block.Header is not XdcBlockHeader xdcHead)
                throw new InvalidOperationException($"Expected an XDC header, but got {e.Block.Header.GetType().FullName}");

            if (xdcHead.ExtraConsensusData is null)
                throw new InvalidOperationException("New head block missing ExtraConsensusData");

            _lastActivityTime = DateTime.UtcNow;
            _channel.TryWrite(new NewHeadEvent(xdcHead));
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
                var epochSwitchInfo = _epochSwitchManager.GetEpochSwitchInfo(currentHead);
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

            if (_logger.IsDebug) _logger.Debug("Stopping XdcHotStuff consensus runner...");

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
            if (_logger.IsInfo) _logger.Info("XdcHotStuff consensus runner stopped.");
        }

        private static bool IsMasternode(EpochSwitchInfo epochInfo, Address node) =>
            epochInfo.Masternodes.AsSpan().IndexOf(node) != -1;
    }
}
