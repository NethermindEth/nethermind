// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Synchronization.Peers;
using Nethermind.Xdc.Errors;
using Nethermind.Xdc.P2P;
using Nethermind.Xdc.RLP;
using Nethermind.Core.Specs;
using Nethermind.Xdc.Spec;
using Nethermind.Xdc.Types;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading.Tasks;

namespace Nethermind.Xdc;

public class TimeoutCertificateManager : ITimeoutCertificateManager
{
    private static readonly EthereumEcdsa _ethereumEcdsa = new(0);
    private static readonly TimeoutDecoder _timeoutDecoder = new();
    private readonly IXdcConsensusContext _consensusContext;
    private readonly ITimeoutTimer _timeoutTimer;
    private readonly ISyncPeerPool _syncPeerPool;
    private readonly ISnapshotManager _snapshotManager;
    private readonly IEpochSwitchManager _epochSwitchManager;
    private readonly ISpecProvider _specProvider;
    private readonly IBlockTree _blockTree;
    private readonly ISigner _signer;
    private readonly ILogger _logger;
    private readonly XdcPool<Timeout> _timeouts = new();
    private readonly ConcurrentDictionary<ulong, byte> _tcBuildStartedByRound = new();

    public TimeoutCertificateManager(
        IXdcConsensusContext context,
        ITimeoutTimer timeoutTimer,
        ISyncPeerPool syncPeerPool,
        ISnapshotManager snapshotManager,
        IEpochSwitchManager epochSwitchManager,
        ISpecProvider specProvider,
        IBlockTree blockTree,
        ISigner signer,
        ILogManager logManager)
    {
        _consensusContext = context;
        this._timeoutTimer = timeoutTimer;
        this._syncPeerPool = syncPeerPool;
        this._snapshotManager = snapshotManager;
        this._epochSwitchManager = epochSwitchManager;
        this._specProvider = specProvider;
        this._blockTree = blockTree;
        this._signer = signer;
        _logger = logManager.GetClassLogger<TimeoutCertificateManager>();
        _timeoutTimer.TimeoutElapsed += (s, e) => OnCountdownTimer();
    }

    public Task HandleTimeoutVote(Timeout timeout)
    {
        if (timeout.Round != _consensusContext.CurrentRound)
        {
            // Not interested in processing timeout for round different from the current one
            return Task.CompletedTask;
        }

        _timeouts.Add(timeout);
        IReadOnlyCollection<Timeout> collectedTimeouts = _timeouts.GetItemsByKey(timeout);

        if (_blockTree.Head?.Header is not XdcBlockHeader xdcHeader)
            return Task.CompletedTask;

        EpochSwitchInfo? epochSwitchInfo = _epochSwitchManager.GetEpochSwitchInfo(xdcHeader);
        if (epochSwitchInfo is null)
        {
            // Failed to get epoch switch info, cannot process timeout
            return Task.CompletedTask;
        }

        BroadcastTimeout(timeout);

        IXdcReleaseSpec spec = _specProvider.GetXdcSpec(xdcHeader, timeout.Round);
        if (collectedTimeouts.Count < epochSwitchInfo.Masternodes.Length * spec.CertificateThreshold)
            return Task.CompletedTask;

        if (!_tcBuildStartedByRound.TryAdd(timeout.Round, 0))
            return Task.CompletedTask;

        OnTimeoutPoolThresholdReached(collectedTimeouts, timeout);
        return Task.CompletedTask;
    }

    private void BroadcastTimeout(Timeout timeout)
    {
        foreach (PeerInfo peer in _syncPeerPool.AllPeers)
        {
            if (peer.SyncPeer is XdcProtocolHandler xdcProtocol)
                xdcProtocol.SendTimeout(timeout);
        }
    }

    private void OnTimeoutPoolThresholdReached(IEnumerable<Timeout> timeouts, Timeout timeout)
    {
        Signature[] signatures = timeouts
            .Where(t => t.Signature is not null)
            .Select(t => t.Signature!)
            .ToArray();

        TimeoutCertificate timeoutCertificate = new(timeout.Round, signatures, timeout.GapNumber);

        ProcessTimeoutCertificate(timeoutCertificate);
    }

    public void ProcessTimeoutCertificate(TimeoutCertificate timeoutCertificate)
    {
        if (_consensusContext.HighestTC is null || timeoutCertificate.Round > _consensusContext.HighestTC.Round)
        {
            _consensusContext.HighestTC = timeoutCertificate;
        }

        if (timeoutCertificate.Round >= _consensusContext.CurrentRound)
        {
            _consensusContext.SetNewRound(timeoutCertificate.Round + 1);
        }

        CleanupTimeouts(timeoutCertificate.Round);
    }

    private void CleanupTimeouts(ulong round)
    {
        _timeouts.EndRound(round);

        foreach (KeyValuePair<ulong, byte> kvp in _tcBuildStartedByRound)
            if (kvp.Key <= round) _tcBuildStartedByRound.TryRemove(kvp.Key, out _);
    }

    public bool VerifyTimeoutCertificate(TimeoutCertificate timeoutCertificate, [NotNullWhen(false)] out string? errorMessage)
    {
        ArgumentNullException.ThrowIfNull(timeoutCertificate);
        if (timeoutCertificate.Signatures is null) throw new ArgumentNullException(nameof(timeoutCertificate.Signatures));

        Snapshot? snapshot = _snapshotManager.GetSnapshotByGapNumber(timeoutCertificate.GapNumber);
        if (snapshot is null)
        {
            errorMessage = $"Failed to get snapshot using gap number {timeoutCertificate.GapNumber}";
            return false;
        }

        if (snapshot.NextEpochCandidates.Length == 0)
        {
            errorMessage = "Empty master node list from snapshot";
            return false;
        }
        if (_blockTree.Head?.Header is not XdcBlockHeader xdcHeader)
        {
            errorMessage = "Failed to get current XDC header";
            return false;
        }

        IXdcReleaseSpec spec = _specProvider.GetXdcSpec(xdcHeader, timeoutCertificate.Round);
        EpochSwitchInfo? epochInfo = _epochSwitchManager.GetTimeoutCertificateEpochInfo(timeoutCertificate);
        if (epochInfo is null)
        {
            errorMessage = $"Failed to get epoch switch info for timeout certificate with round {timeoutCertificate.Round}";
            return false;
        }

        double required = epochInfo.Masternodes.Length * spec.CertificateThreshold;
        (Address[] candidates, Signature[] signatures) = (snapshot.NextEpochCandidates, timeoutCertificate.Signatures);
        if (signatures.Length < required)
        {
            errorMessage = $"Number of signatures ({signatures.Length}) does not meet threshold of {required}";
            return false;
        }

        ValueHash256 timeoutMsgHash = ComputeTimeoutMsgHash(timeoutCertificate.Round, timeoutCertificate.GapNumber);
        if (VotesManager.CountValidSignatures(candidates, signatures, timeoutMsgHash, out errorMessage) is not { } signCount)
        {
            errorMessage ??= "Timeout certificate contains invalid signatures.";
            return false;
        }

        if (signCount < epochInfo.Masternodes.Length * spec.CertificateThreshold)
        {
            errorMessage = $"Number of unique signers {signCount} does not meet threshold of {epochInfo.Masternodes.Length * spec.CertificateThreshold}";
            return false;
        }

        errorMessage = null;
        return true;
    }

    public void OnCountdownTimer()
    {
        try
        {
            if (!AllowedToSend())
                return;

            SendTimeout();
            _consensusContext.TimeoutCounter++;

            if (_blockTree.Head?.Header is not XdcBlockHeader xdcHeader)
                return;

            IXdcReleaseSpec spec = _specProvider.GetXdcSpec(xdcHeader, _consensusContext.CurrentRound);

            if (_consensusContext.TimeoutCounter % spec.TimeoutSyncThreshold == 0)
            {
                SyncInfo syncInfo = GetSyncInfo();
                foreach (PeerInfo peerInfo in _syncPeerPool.AllPeers)
                {
                    if (peerInfo.SyncPeer is XdcProtocolHandler xdcProtocolHandler)
                        xdcProtocolHandler.SendSyncInfo(syncInfo);
                }
            }
        }
        finally
        {
            ResetTimer();
        }
    }

    private void ResetTimer()
    {
        if (_blockTree.Head?.Header is not XdcBlockHeader xdcHeader)
            return;

        IXdcReleaseSpec spec = _specProvider.GetXdcSpec(xdcHeader, _consensusContext.CurrentRound);
        _timeoutTimer.Reset(TimeSpan.FromSeconds(spec.TimeoutPeriod));
    }

    public Task OnReceiveTimeout(Timeout timeout)
    {
        Block currentBlock = _blockTree.Head ?? throw new InvalidOperationException("Failed to get current block");
        if (currentBlock.Header is not XdcBlockHeader currentHeader)
        {
            return Task.CompletedTask;
        }

        ulong currentBlockNumber = currentBlock.Number;
        ulong epochLength = _specProvider.GetXdcSpec(currentHeader, timeout.Round).EpochLength;

        ulong gapDiff = timeout.GapNumber > currentBlockNumber
            ? timeout.GapNumber - currentBlockNumber
            : currentBlockNumber - timeout.GapNumber;

        if (gapDiff > 3 * epochLength)
        {
            // Discarded propagated timeout, too far away
            return Task.CompletedTask;
        }

        if (FilterTimeout(timeout))
        {
            //TODO: Broadcast Timeout
            return HandleTimeoutVote(timeout);
        }
        return Task.CompletedTask;
    }

    internal bool FilterTimeout(Timeout timeout)
    {
        if (timeout.Round < _consensusContext.CurrentRound) return false;
        Snapshot? snapshot = _snapshotManager.GetSnapshotByGapNumber(timeout.GapNumber);
        if (snapshot is null || snapshot.NextEpochCandidates.Length == 0) return false;
        if (timeout.Signature is null) return false;

        // Verify msg signature
        ValueHash256 timeoutMsgHash = ComputeTimeoutMsgHash(timeout.Round, timeout.GapNumber);
        Address? signer = _ethereumEcdsa.RecoverAddress(timeout.Signature, in timeoutMsgHash);
        if (signer is null) return false;
        timeout.Signer = signer;

        return snapshot.NextEpochCandidates.Contains(signer);
    }

    internal SyncInfo GetSyncInfo() => new(_consensusContext.HighestQC, _consensusContext.HighestTC);

    private void SendTimeout()
    {
        if (_blockTree.Head?.Header is not XdcBlockHeader currentHeader)
        {
            throw new InvalidOperationException("Failed to retrieve current header");
        }
        ulong currentRound = _consensusContext.CurrentRound;
        IXdcReleaseSpec spec = _specProvider.GetXdcSpec(currentHeader, currentRound);

        ulong gapNumber;
        if (_epochSwitchManager.IsEpochSwitchAtRound(currentRound, currentHeader))
        {
            ulong currentNumber = currentHeader.Number + 1;
            ulong offset = currentNumber % spec.EpochLength + spec.Gap;
            gapNumber = currentNumber.SaturatingSub(offset);
        }
        else
        {
            EpochSwitchInfo epochSwitchInfo = _epochSwitchManager.GetEpochSwitchInfo(currentHeader)
                ?? throw new DataExtractionException(nameof(EpochSwitchInfo));
            ulong currentNumber = epochSwitchInfo.EpochSwitchBlockInfo.BlockNumber;
            ulong offset = currentNumber % spec.EpochLength + spec.Gap;
            gapNumber = currentNumber.SaturatingSub(offset);
        }

        ValueHash256 msgHash = ComputeTimeoutMsgHash(currentRound, gapNumber);
        if (!_signer.TrySign(in msgHash, out Signature signedHash))
        {
            if (_logger.IsWarn) _logger.Warn($"XDC signer {_signer.Address} could not sign timeout for round {currentRound} — skipping broadcast.");
            return;
        }
        Timeout timeoutMsg = new(currentRound, signedHash, gapNumber, isMyVote: true);
        timeoutMsg.Signer = _signer.Address;

        HandleTimeoutVote(timeoutMsg);
    }

    // Returns true if the signer is within the master node list
    private bool AllowedToSend()
    {
        if (_blockTree.Head?.Header is not XdcBlockHeader currentHeader)
            return false;

        EpochSwitchInfo? epochSwitchInfo = _epochSwitchManager.GetEpochSwitchInfo(currentHeader);
        if (epochSwitchInfo is null)
            return false;
        return epochSwitchInfo.Masternodes.Contains(_signer.Address);
    }

    internal static ValueHash256 ComputeTimeoutMsgHash(ulong round, ulong gap)
    {
        Timeout timeout = new(round, null, gap);
        KeccakRlpWriter writer = new();
        _timeoutDecoder.Encode(ref writer, timeout, RlpBehaviors.ForSealing);
        return writer.GetValueHash();
    }

    public long GetTimeoutsCount(Timeout timeout) => _timeouts.GetCount(timeout);
    public IDictionary<(ulong Round, Hash256 Hash), Dictionary<Address, Timeout>> GetReceivedTimeouts() => _timeouts.GetItems();
}
