// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Xdc.Spec;
using Nethermind.Xdc.Types;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.Xdc
{
    /// <summary>
    /// Orchestrates the consensus loop: leader block proposal, voting, QC aggregation,
    /// timeout handling, and 3-chain finalization.
    /// </summary>
    internal class XdcHotStuff(
        IBlockTree blockTree,
        IXdcConsensusContext xdcContext,
        ISpecProvider specProvider,
        IBlockProducer blockBuilder,
        IEpochSwitchManager epochSwitchManager,
        IMasternodesCalculator masternodesCalculator,
        IVotesManager votesManager,
        ISigner signer,
        ITimeoutTimer timeoutTimer,
        ISignTransactionManager signTransactionManager,
        ILogManager logManager) : IBlockProducerRunner
    {
        private readonly IBlockTree _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
        private readonly IXdcConsensusContext _xdcContext = xdcContext;
        private readonly ISpecProvider _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
        private readonly IBlockProducer _blockBuilder = blockBuilder ?? throw new ArgumentNullException(nameof(blockBuilder));
        private readonly IEpochSwitchManager _epochSwitchManager = epochSwitchManager ?? throw new ArgumentNullException(nameof(epochSwitchManager));
        private readonly IMasternodesCalculator _masternodesCalculator = masternodesCalculator ?? throw new ArgumentNullException(nameof(masternodesCalculator));
        private readonly IVotesManager _votesManager = votesManager ?? throw new ArgumentNullException(nameof(votesManager));
        private readonly ISigner _signer = signer ?? throw new ArgumentNullException(nameof(signer));
        private readonly ITimeoutTimer _timeoutTimer = timeoutTimer;
        private readonly ILogger _logger = logManager?.GetClassLogger<XdcHotStuff>() ?? throw new ArgumentNullException(nameof(logManager));
        private readonly ISignTransactionManager _signTransactionManager = signTransactionManager ?? throw new ArgumentNullException(nameof(signTransactionManager));

        private readonly object _lockObject = new();
        private CancellationTokenSource? _cancellationTokenSource;
        private CancellationTokenSource? _roundCts;

        private DateTime _lastActivityTime = DateTime.UtcNow;
        private long _highestSelfMinedRound;
        private long _highestVotedRound;

        public event EventHandler<BlockEventArgs>? BlockProduced;

        // ── Lifecycle ────────────────────────────────────────────────────────────

        public void Start()
        {
            lock (_lockObject)
            {
                if (_cancellationTokenSource is not null)
                {
                    _logger.Info("XdcHotStuff already started, ignoring duplicate Start() call");
                    return;
                }

                _cancellationTokenSource = new CancellationTokenSource();
                _blockTree.NewHeadBlock += OnNewHeadBlock;
                _xdcContext.NewRoundSetEvent += OnNewRound;
                _logger.Info("XdcHotStuff consensus runner started");
            }
        }

        public Task StopAsync()
        {
            lock (_lockObject)
            {
                if (_cancellationTokenSource is null) return Task.CompletedTask;

                _blockTree.NewHeadBlock -= OnNewHeadBlock;
                _xdcContext.NewRoundSetEvent -= OnNewRound;

                _cancellationTokenSource.Cancel();
                _cancellationTokenSource.Dispose();
                _cancellationTokenSource = null;

                _roundCts?.Cancel();
                _roundCts?.Dispose();
                _roundCts = null;
            }

            _logger.Info("XdcHotStuff consensus runner stopped");
            return Task.CompletedTask;
        }

        public bool IsProducingBlocks(ulong? maxProducingInterval)
        {
            if (!maxProducingInterval.HasValue)
                return _cancellationTokenSource is not null && !_cancellationTokenSource.IsCancellationRequested;

            TimeSpan elapsed = DateTime.UtcNow - _lastActivityTime;
            TimeSpan maxInterval = TimeSpan.FromSeconds(maxProducingInterval.Value);
            return elapsed <= maxInterval;
        }

        // ── Event handlers ───────────────────────────────────────────────────────

        private void OnNewHeadBlock(object? sender, BlockEventArgs e)
        {
            if (e.Block.Header is not XdcBlockHeader xdcHead)
                throw new InvalidOperationException($"Expected XdcBlockHeader but got {e.Block.Header.GetType().FullName}");
            if (xdcHead.ExtraConsensusData is null) return;
            if (_blockTree.IsSyncing().isSyncing) return;

            _lastActivityTime = DateTime.UtcNow;

            StartRoundTask(xdcHead, _xdcContext.CurrentRound);
        }

        private void OnNewRound(object sender, NewRoundEventArgs args)
        {
            if (args.LastRoundDuration is { } lastRoundDuration)
                _logger.Info($"Round {args.PreviousRound} completed in {lastRoundDuration.TotalSeconds:F2}s");

            ulong currentRound = _xdcContext.CurrentRound;
            if (args.NewRound != currentRound) return;

            if (_blockTree.Head?.Header is not XdcBlockHeader head)
                throw new InvalidOperationException("BlockTree head is not XdcBlockHeader.");
            if (_blockTree.IsSyncing().isSyncing) return;

            _lastActivityTime = DateTime.UtcNow;

            IXdcReleaseSpec spec = _specProvider.GetXdcSpec(head, currentRound);
            _timeoutTimer.Reset(TimeSpan.FromSeconds(spec.TimeoutPeriod));

            StartRoundTask(head, currentRound);
        }

        // ── Round task ───────────────────────────────────────────────────────────

        private void StartRoundTask(XdcBlockHeader head, ulong round)
        {
            CancellationToken token;
            lock (_lockObject)
            {
                if (_cancellationTokenSource is null) return;

                _roundCts?.Cancel();
                _roundCts?.Dispose();
                _roundCts = CancellationTokenSource.CreateLinkedTokenSource(_cancellationTokenSource.Token);
                token = _roundCts.Token;
            }
            _ = RunRound(head, round, token); // fire-and-forget: runs as background round task
        }

        private async Task RunRound(XdcBlockHeader head, ulong round, CancellationToken ct)
        {
            try
            {
                _logger.Info($"Round {round}: Checking header #{head.Number}, hash={head.Hash}, round={(long)head.ExtraConsensusData.BlockRound}");

                EpochSwitchInfo? epochInfo = _epochSwitchManager.GetEpochSwitchInfo(head);
                if (epochInfo?.Masternodes is null || epochInfo.Masternodes.Length == 0) return;

                IXdcReleaseSpec spec = _specProvider.GetXdcSpec(head, round);

                if (spec.SwitchBlock < head.Number)
                    await Vote(head, epochInfo);

                // Cast vote might advance round
                if (ct.IsCancellationRequested) return;

                // Proposal path: always build on the highest certified block, not necessarily head —
                // QC can arrive via P2P before the block itself, leaving head behind.
                QuorumCertificate? qc = _xdcContext.HighestQC;
                if (qc is null) return;

                if (_blockTree.FindHeader(qc.ProposedBlockInfo.Hash, qc.ProposedBlockInfo.BlockNumber)
                    is not XdcBlockHeader proposalParent) return;

                EpochSwitchInfo? proposalEpochInfo = _epochSwitchManager.GetEpochSwitchInfo(proposalParent);
                if (proposalEpochInfo?.Masternodes is null || proposalEpochInfo.Masternodes.Length == 0) return;

                IXdcReleaseSpec proposalSpec = _specProvider.GetXdcSpec(proposalParent, round);

                if (!IsMyTurn(proposalParent, round, proposalSpec)) return;

                if (!TryAdvance(ref _highestSelfMinedRound, (long)round)) return;

                _logger.Info($"Round {round}: I am leader, committee={proposalEpochInfo.Masternodes.Length}, parent=#{proposalParent.Number}");

                // Gate 1: enforce minimum mine period since parent block was produced
                long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                long mineReadyAt = (long)proposalParent.Timestamp + proposalSpec.MinePeriod;
                if (mineReadyAt > now)
                    await Task.Delay(TimeSpan.FromSeconds(mineReadyAt - now), ct);

                if (ct.IsCancellationRequested) return;

                // Gate 2: if head has no QC yet, wait for late votes to form one.
                // If QC arrives, NewRoundSetEvent fires and cancels this task via ct.
                // If fallback elapses without QC, propose on the last certified block.
                bool headHasQc = head.Hash == qc.ProposedBlockInfo.Hash;
                if (!headHasQc)
                {
                    long fallbackReadyAt = (long)head.Timestamp + proposalSpec.TimeoutPeriod / 2;
                    now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    if (fallbackReadyAt > now)
                        await Task.Delay(TimeSpan.FromSeconds(fallbackReadyAt - now), ct);
                }

                if (ct.IsCancellationRequested) return;

                await BuildAndProposeBlock(proposalParent, qc, round, proposalSpec, ct);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.Error($"RunRound failed for round {round}", ex);
            }
        }

        // ── Block proposal ───────────────────────────────────────────────────────

        internal async Task BuildAndProposeBlock(XdcBlockHeader parent, QuorumCertificate qc, ulong currentRound, IXdcReleaseSpec spec, CancellationToken ct)
        {
            try
            {
                _logger.Debug($"Round {currentRound}: Building proposal block on #{parent.Number}");

                XdcPayloadAttributes payloadAttributes = new()
                {
                    Round = currentRound,
                    QuorumCertificate = qc,
                    Timestamp = (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                };

                Block? proposedBlock = await _blockBuilder.BuildBlock(parent, null, payloadAttributes, IBlockProducer.Flags.None, ct);

                if (proposedBlock is not null)
                {
                    _lastActivityTime = DateTime.UtcNow;
                    _logger.Info($"Round {currentRound}: Block #{proposedBlock.Number} built successfully, hash={proposedBlock.Hash}");
                    BlockProduced?.Invoke(this, new BlockEventArgs(proposedBlock));
                }
                else
                {
                    _logger.Warn($"Round {currentRound}: Block builder returned null");
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.Error($"Failed to build block in round {currentRound}", ex);
            }
        }

        // ── Voting ───────────────────────────────────────────────────────────────

        private async Task Vote(XdcBlockHeader head, EpochSwitchInfo epochInfo)
        {
            if (head.ExtraConsensusData?.QuorumCert is null)
                throw new InvalidOperationException("Head block missing consensus data.");

            long votingRound = (long)head.ExtraConsensusData.BlockRound;

            if (!IsMasternode(epochInfo, _signer.Address))
            {
                if (_logger.IsDebug)
                    _logger.Debug($"Round {votingRound}: Skipped voting (not in masternode set)");
                return;
            }

            if(votingRound <= _highestVotedRound) return;

            if (!_votesManager.VerifyVotingRules(head, out string? error))
            {
                if (_logger.IsDebug)
                    _logger.Debug($"Round {votingRound}: Voting rule not satisfied for block #{head.Number}, hash={head.Hash}: {error}");
                return;
            }

            if (!TryAdvance(ref _highestVotedRound, votingRound)) return;

            if (_logger.IsInfo)
                _logger.Info($"Round {votingRound}: Voting for block #{head.Number}, hash={head.Hash}");

            try
            {
                BlockRoundInfo voteInfo = new(head.Hash!, head.ExtraConsensusData.BlockRound, head.Number);
                await _votesManager.CastVote(voteInfo);
                _lastActivityTime = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                _logger.Error($"Round {votingRound}: Failed to cast vote.", ex);
            }
        }

        private static bool TryAdvance(ref long field, long value)
        {
            long current;
            do
            {
                current = Interlocked.Read(ref field);
                if (current >= value) return false;
            } while (Interlocked.CompareExchange(ref field, value, current) != current);
            return true;
        }

        // ── Leader selection ─────────────────────────────────────────────────────

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
                (currentMasternodes, _) = _masternodesCalculator.CalculateNextEpochMasternodes(currentHead.Number + 1, currentHead.Hash, spec);
            }
            else
            {
                EpochSwitchInfo epochSwitchInfo = _epochSwitchManager.GetEpochSwitchInfo(currentHead);
                currentMasternodes = epochSwitchInfo.Masternodes;
            }

            int currentLeaderIndex = (int)round % spec.EpochLength % currentMasternodes.Length;
            return currentMasternodes[currentLeaderIndex];
        }

        private static bool IsMasternode(EpochSwitchInfo epochInfo, Address node) =>
            epochInfo.Masternodes.AsSpan().IndexOf(node) != -1;
    }
}
