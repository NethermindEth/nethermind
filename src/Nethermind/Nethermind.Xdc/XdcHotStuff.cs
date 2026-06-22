// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
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
        ITimestamper timestamper,
        ILogManager logManager) : IBlockProducerRunner
    {
        private readonly IBlockTree _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
        private readonly IXdcConsensusContext _xdcContext = xdcContext ?? throw new ArgumentNullException(nameof(xdcContext));
        private readonly ISpecProvider _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
        private readonly IBlockProducer _blockBuilder = blockBuilder ?? throw new ArgumentNullException(nameof(blockBuilder));
        private readonly IEpochSwitchManager _epochSwitchManager = epochSwitchManager ?? throw new ArgumentNullException(nameof(epochSwitchManager));
        private readonly IMasternodesCalculator _masternodesCalculator = masternodesCalculator ?? throw new ArgumentNullException(nameof(masternodesCalculator));
        private readonly IVotesManager _votesManager = votesManager ?? throw new ArgumentNullException(nameof(votesManager));
        private readonly ISigner _signer = signer ?? throw new ArgumentNullException(nameof(signer));
        private readonly ITimeoutTimer _timeoutTimer = timeoutTimer ?? throw new ArgumentNullException(nameof(timeoutTimer));
        private readonly ITimestamper _timestamper = timestamper ?? throw new ArgumentNullException(nameof(timestamper));
        private readonly ILogger _logger = logManager?.GetClassLogger<XdcHotStuff>() ?? throw new ArgumentNullException(nameof(logManager));

        private readonly object _lockObject = new();
        private volatile bool _running;
        private CancellationTokenSource? _roundCts;
        private Task? _roundTask;

        private DateTime _lastActivityTime = DateTime.UtcNow;
        private ulong _highestSelfMinedRound;
        private ulong _highestVotedRound;
        private ulong _lastStartedRound = ulong.MaxValue;
        private ulong _pendingPrevRound;
        private TimeSpan? _pendingLastRoundDuration;

        public event EventHandler<BlockEventArgs>? BlockProduced;

        // ── Lifecycle ────────────────────────────────────────────────────────────

        public void Start()
        {
            lock (_lockObject)
            {
                if (_running)
                {
                    _logger.Info("XdcHotStuff already started, ignoring duplicate Start() call");
                    return;
                }

                _running = true;
                _blockTree.NewHeadBlock += OnNewHeadBlock;
                _xdcContext.NewRoundSetEvent += OnNewRound;
                _logger.Info("XdcHotStuff consensus runner started");
            }

            if (IsSynced() && _xdcContext.CurrentRound != 0)
            {
                XdcBlockHeader head = (XdcBlockHeader)_blockTree.Head!.Header;
                StartRoundTask(head, _xdcContext.CurrentRound);
            }
        }

        public async Task StopAsync()
        {
            Task? runningTask;
            lock (_lockObject)
            {
                if (!_running) return;

                _running = false;
                _blockTree.NewHeadBlock -= OnNewHeadBlock;
                _xdcContext.NewRoundSetEvent -= OnNewRound;

                _roundCts?.Cancel();
                _roundCts?.Dispose();
                _roundCts = null;
                runningTask = _roundTask;
                _roundTask = null;
            }

            if (runningTask is not null)
            {
                try { await runningTask; }
                catch (OperationCanceledException) { }
            }

            _logger.Info("XdcHotStuff consensus runner stopped");
        }

        public bool IsProducingBlocks(ulong? maxProducingInterval)
        {
            if (!maxProducingInterval.HasValue)
                return _running;

            TimeSpan elapsed = DateTime.UtcNow - _lastActivityTime;
            TimeSpan maxInterval = TimeSpan.FromSeconds(maxProducingInterval.Value);
            return elapsed <= maxInterval;
        }

        // ── Event handlers ───────────────────────────────────────────────────────

        private void OnNewHeadBlock(object? sender, BlockEventArgs e)
        {
            if (!IsSynced()) return;

            _lastActivityTime = DateTime.UtcNow;

            XdcBlockHeader xdcHead = (XdcBlockHeader)e.Block.Header;
            StartRoundTask(xdcHead, _xdcContext.CurrentRound);
        }

        private void OnNewRound(object? sender, NewRoundEventArgs args)
        {
            if (!IsSynced()) return;

            _lastActivityTime = DateTime.UtcNow;

            XdcBlockHeader head = (XdcBlockHeader)_blockTree.Head!.Header;
            ulong currentRound = args.NewRound;
            _pendingPrevRound = args.PreviousRound;
            _pendingLastRoundDuration = args.LastRoundDuration;

            IXdcReleaseSpec spec = _specProvider.GetXdcSpec(head, currentRound);
            _timeoutTimer.Reset(TimeSpan.FromSeconds(spec.TimeoutPeriod));

            StartRoundTask(head, args.NewRound);
        }

        // ── Round task ───────────────────────────────────────────────────────────

        internal void StartRoundTask(XdcBlockHeader head, ulong round)
        {
            CancellationToken token;
            lock (_lockObject)
            {
                if (!_running) return;

                _roundCts?.Cancel();
                _roundCts?.Dispose();
                _roundCts = new CancellationTokenSource();
                token = _roundCts.Token;
            }

            Task newTask = RunRound(head, round, token);

            lock (_lockObject)
            {
                if (_running)
                    _roundTask = newTask;
            }
        }

        private async Task RunRound(XdcBlockHeader head, ulong round, CancellationToken ct)
        {
            try
            {
                LogRoundAdvance(round, head);

                EpochSwitchInfo? epochInfo = _epochSwitchManager.GetEpochSwitchInfo(head);
                if (epochInfo?.Masternodes is null || epochInfo.Masternodes.Length == 0) return;

                IXdcReleaseSpec spec = _specProvider.GetXdcSpec(head, round);

                if (spec.SwitchBlock < head.Number)
                    await Vote(head, epochInfo);

                // Voting may advance the round, which cancels this task.
                if (ct.IsCancellationRequested) return;

                await TryPropose(head, round, ct);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.Error($"RunRound failed for round {round}", ex);
            }
        }

        private async Task TryPropose(XdcBlockHeader head, ulong round, CancellationToken ct)
        {
            // Always build on the highest certified block, not necessarily head
            QuorumCertificate? qc = _xdcContext.HighestQC;
            if (qc is null) return;

            if (_blockTree.FindHeader(qc.ProposedBlockInfo.Hash, qc.ProposedBlockInfo.BlockNumber)
                is not XdcBlockHeader proposalParent) return;

            EpochSwitchInfo? proposalEpochInfo = _epochSwitchManager.GetEpochSwitchInfo(proposalParent);
            if (proposalEpochInfo?.Masternodes is null || proposalEpochInfo.Masternodes.Length == 0) return;

            IXdcReleaseSpec proposalSpec = _specProvider.GetXdcSpec(proposalParent, round);

            if (!IsMyTurn(proposalParent, round, proposalSpec)) return;

            if (!TryAdvance(ref _highestSelfMinedRound, round)) return;

            // Gate 1: enforce minimum mine period since parent block was produced
            ulong now = _timestamper.UnixTime.Seconds;
            ulong mineReadyAt = proposalParent.Timestamp + proposalSpec.MinePeriod;
            if (mineReadyAt > now)
                await Task.Delay(TimeSpan.FromSeconds(mineReadyAt - now), ct);

            if (ct.IsCancellationRequested) return;

            // Gate 2: if head has no QC yet, wait for late votes to form one.
            // If QC arrives, NewRoundSetEvent fires and cancels this task via ct.
            // If fallback elapses without QC, propose on the last certified block.
            bool headHasQc = head.Hash == qc.ProposedBlockInfo.Hash;
            if (!headHasQc)
            {
                ulong fallbackReadyAt = head.Timestamp + (ulong)proposalSpec.TimeoutPeriod / 2;
                now = _timestamper.UnixTime.Seconds;
                if (fallbackReadyAt > now)
                    await Task.Delay(TimeSpan.FromSeconds(fallbackReadyAt - now), ct);
            }

            if (ct.IsCancellationRequested) return;

            await BuildAndProposeBlock(proposalParent, qc, round, proposalSpec, ct);
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
                    Timestamp = _timestamper.UnixTime.Seconds
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

            ulong votingRound = head.ExtraConsensusData.BlockRound;

            if (!IsMasternode(epochInfo, _signer.Address))
            {
                if (_logger.IsDebug)
                    _logger.Debug($"Round {votingRound}: Skipped voting (not in masternode set)");
                return;
            }

            if (votingRound <= _highestVotedRound) return;

            if (!_votesManager.VerifyVotingRules(head, out string? error))
            {
                if (_logger.IsDebug)
                    _logger.Debug($"Round {votingRound}: Voting rule not satisfied for block #{head.Number}, hash={head.Hash}: {error}");
                return;
            }

            if (!TryAdvance(ref _highestVotedRound, votingRound)) return;

            if (_logger.IsDebug)
                _logger.Debug($"Round {votingRound}: Voting for block #{head.Number}, hash={head.Hash}");

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

        private void LogRoundAdvance(ulong round, XdcBlockHeader head)
        {
            if (!TryAdvance(ref _lastStartedRound, round)) return;

            Address? leader = null;
            bool isMyTurn = false;
            int committee = 0;

            QuorumCertificate? qc = _xdcContext.HighestQC;
            if (qc is not null
                && _blockTree.FindHeader(qc.ProposedBlockInfo.Hash, qc.ProposedBlockInfo.BlockNumber) is XdcBlockHeader proposalParent
                && _epochSwitchManager.GetEpochSwitchInfo(proposalParent) is { Masternodes.Length: > 0 } epochInfo)
            {
                IXdcReleaseSpec spec = _specProvider.GetXdcSpec(proposalParent, round);
                leader = GetLeaderAddress(proposalParent, round, spec);
                isMyTurn = leader == _signer.Address;
                committee = epochInfo.Masternodes.Length;
            }

            string headInfo = $"#{head.Number} round={head.ExtraConsensusData?.BlockRound} ({head.Hash?.ToShortString()})";
            string roundDuration = _pendingLastRoundDuration.HasValue ? $", prev={_pendingPrevRound} in {_pendingLastRoundDuration.Value.TotalSeconds:F2}s" : "";
            string myTurn = isMyTurn ? "true" : "false";
            _logger.Info($"Round {round}{roundDuration}: head={headInfo} | Leader={leader?.ToShortString()}, MyTurn={myTurn}, Committee={committee} nodes");
        }

        private static bool TryAdvance(ref ulong field, ulong value)
        {
            ulong current;
            do
            {
                current = Interlocked.CompareExchange(ref field, field, field);
                if (current != ulong.MaxValue && current >= value) return false;
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

            int currentLeaderIndex = (int)(round % spec.EpochLength % (ulong)currentMasternodes.Length);
            return currentMasternodes[currentLeaderIndex];
        }

        private static bool IsMasternode(EpochSwitchInfo epochInfo, Address node) =>
            epochInfo.Masternodes.AsSpan().IndexOf(node) != -1;

        // TODO: consider using a another sync indicator
        private bool IsSynced() => !_blockTree.IsSyncing().isSyncing && _blockTree.Head is not null;
    }
}
