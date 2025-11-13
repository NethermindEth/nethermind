// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Serialization.Rlp;
using Nethermind.Xdc.RLP;
using Nethermind.Xdc.Errors;
using Nethermind.Xdc.Types;
using Nethermind.Xdc.Spec;

namespace Nethermind.Xdc;

public class TimeoutCertificateManager : ITimeoutCertificateManager
{
    private EthereumEcdsa _ethereumEcdsa = new EthereumEcdsa(0);
    private static readonly TimeoutDecoder _timeoutDecoder = new();
    private readonly IXdcConsensusContext _consensusContext;
    private readonly ISnapshotManager _snapshotManager;
    private readonly IEpochSwitchManager _epochSwitchManager;
    private readonly ISpecProvider _specProvider;
    private readonly IBlockTree _blockTree;
    private readonly ISyncInfoManager _syncInfoManager;
    private readonly ISigner _signer;
    private XdcPool<Timeout> _timeouts = new();

    public TimeoutCertificateManager(IXdcConsensusContext context, ISnapshotManager snapshotManager, IEpochSwitchManager epochSwitchManager, ISpecProvider specProvider, IBlockTree blockTree, ISyncInfoManager syncInfoManager, ISigner signer)
    {
        _consensusContext = context;
        this._snapshotManager = snapshotManager;
        this._epochSwitchManager = epochSwitchManager;
        this._specProvider = specProvider;
        this._blockTree = blockTree;
        this._syncInfoManager = syncInfoManager;
        this._signer = signer;
    }

    public Task HandleTimeoutVote(Timeout timeout)
    {
        if (timeout.Round != _consensusContext.CurrentRound)
        {
            // Not interested in processing timeout for round different from the current one
            return Task.CompletedTask;
        }

        _timeouts.Add(timeout);
        var collectedTimeouts = _timeouts.GetItems(timeout);

        var xdcHeader = _blockTree.Head?.Header as XdcBlockHeader;
        EpochSwitchInfo epochSwitchInfo = _epochSwitchManager.GetEpochSwitchInfo(xdcHeader);
        if (epochSwitchInfo is null)
        {
            // Failed to get epoch switch info, cannot process timeout
            return Task.CompletedTask;
        }

        IXdcReleaseSpec spec = _specProvider.GetXdcSpec(xdcHeader, timeout.Round);
        var certThreshold = spec.CertThreshold;
        if (collectedTimeouts.Count >= epochSwitchInfo.Masternodes.Length * certThreshold)
        {
            OnTimeoutPoolThresholdReached(collectedTimeouts, timeout);
        }
        return Task.CompletedTask;
    }



    private void OnTimeoutPoolThresholdReached(IEnumerable<Timeout> timeouts, Timeout timeout)
    {
        Signature[] signatures = timeouts.Select(t => t.Signature).ToArray();

        var timeoutCertificate = new TimeoutCertificate(timeout.Round, signatures, timeout.GapNumber);

        ProcessTimeoutCertificate(timeoutCertificate);

        SyncInfo syncInfo = _syncInfoManager.GetSyncInfo();
        //TODO: Broadcast syncInfo
    }

    public void ProcessTimeoutCertificate(TimeoutCertificate timeoutCertificate)
    {
        if (_consensusContext.HighestTC is null || timeoutCertificate.Round > _consensusContext.HighestTC.Round)
        {
            _consensusContext.HighestTC = timeoutCertificate;
        }

        if (timeoutCertificate.Round >= _consensusContext.CurrentRound)
        {
            _timeouts.EndRound(timeoutCertificate.Round);
            _consensusContext.SetNewRound(timeoutCertificate.Round + 1);
        }
    }

    public bool VerifyTimeoutCertificate(TimeoutCertificate timeoutCertificate, out string errorMessage)
    {
        if (timeoutCertificate is null) throw new ArgumentNullException(nameof(timeoutCertificate));
        if (timeoutCertificate.Signatures is null) throw new ArgumentNullException(nameof(timeoutCertificate.Signatures));

        XdcBlockHeader xdcHeader = _blockTree.Head?.Header as XdcBlockHeader;
        IXdcReleaseSpec spec = _specProvider.GetXdcSpec(xdcHeader, timeoutCertificate.Round);
        Snapshot snapshot = _snapshotManager.GetSnapshot((long)timeoutCertificate.GapNumber, spec);
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
        var nextEpochCandidates = new HashSet<Address>(snapshot.NextEpochCandidates);

        var signatures = new HashSet<Signature>(timeoutCertificate.Signatures);

        EpochSwitchInfo epochInfo = _epochSwitchManager.GetTimeoutCertificateEpochInfo(timeoutCertificate);
        if (epochInfo is null)
        {
            errorMessage = $"Failed to get epoch switch info for timeout certificate with round {timeoutCertificate.Round}";
            return false;
        }
        if (signatures.Count < epochInfo.Masternodes.Length * spec.CertThreshold)
        {
            errorMessage = $"Number of unique signatures {signatures.Count} does not meet threshold of {epochInfo.Masternodes.Length * spec.CertThreshold}";
            return false;
        }

        ValueHash256 timeoutMsgHash = ComputeTimeoutMsgHash(timeoutCertificate.Round, timeoutCertificate.GapNumber);
        bool allValid = true;
        Parallel.ForEach(signatures,
            (signature, state) =>
            {
                Address signer = _ethereumEcdsa.RecoverAddress(signature, in timeoutMsgHash);
                if (!nextEpochCandidates.Contains(signer))
                {
                    allValid = false;
                    state.Stop();
                }
            });
        if (!allValid)
        {
            errorMessage = "One or more invalid signatures";
            return false;
        }

        errorMessage = null;
        return true;
    }

    public void OnCountdownTimer()
    {
        if (!AllowedToSend())
            return;

        SendTimeout();
        _consensusContext.TimeoutCounter++;

        var xdcHeader = _blockTree.Head?.Header as XdcBlockHeader;
        IXdcReleaseSpec spec = _specProvider.GetXdcSpec(xdcHeader!, _consensusContext.CurrentRound);

        if (_consensusContext.TimeoutCounter % spec.TimeoutSyncThreshold == 0)
        {
            SyncInfo syncInfo = _syncInfoManager.GetSyncInfo();
            //TODO: Broadcast syncInfo
        }
    }

    public Task OnReceiveTimeout(Timeout timeout)
    {
        var currentBlock = _blockTree.Head ?? throw new InvalidOperationException("Failed to get current block");
        var currentHeader = currentBlock.Header as XdcBlockHeader;
        var currentBlockNumber = currentBlock.Number;
        var epochLength = _specProvider.GetXdcSpec(currentHeader, timeout.Round).EpochLength;
        if (Math.Abs((long)timeout.GapNumber - currentBlockNumber) > 3 * epochLength)
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

        XdcBlockHeader xdcHeader = _blockTree.Head?.Header as XdcBlockHeader;
        IXdcReleaseSpec spec = _specProvider.GetXdcSpec(xdcHeader, timeout.Round);
        //TODO: getSnapshot method should receive block number instead of gapnumber
        Snapshot snapshot = _snapshotManager.GetSnapshot((long)timeout.GapNumber, spec);
        if (snapshot is null || snapshot.NextEpochCandidates.Length == 0) return false;

        // Verify msg signature
        ValueHash256 timeoutMsgHash = ComputeTimeoutMsgHash(timeout.Round, timeout.GapNumber);
        Address signer = _ethereumEcdsa.RecoverAddress(timeout.Signature, in timeoutMsgHash);
        timeout.Signer = signer;

        return snapshot.NextEpochCandidates.Contains(signer);
    }

    private void SendTimeout()
    {
        long gapNumber = 0;
        var currentHeader = (XdcBlockHeader)_blockTree.Head?.Header;
        if (currentHeader is null) throw new InvalidOperationException("Failed to retrieve current header");
        IXdcReleaseSpec spec = _specProvider.GetXdcSpec(currentHeader, _consensusContext.CurrentRound);
        if (_epochSwitchManager.IsEpochSwitchAtRound(_consensusContext.CurrentRound, currentHeader))
        {
            var currentNumber = currentHeader.Number + 1;
            gapNumber = Math.Max(0, currentNumber - currentNumber % spec.EpochLength - spec.Gap);
        }
        else
        {
            EpochSwitchInfo epochSwitchInfo = _epochSwitchManager.GetEpochSwitchInfo(currentHeader);
            if (epochSwitchInfo is null)
                throw new DataExtractionException(nameof(EpochSwitchInfo));

            var currentNumber = epochSwitchInfo.EpochSwitchBlockInfo.BlockNumber;
            gapNumber = Math.Max(0, currentNumber - currentNumber % spec.EpochLength - spec.Gap);
        }

        ValueHash256 msgHash = ComputeTimeoutMsgHash(_consensusContext.CurrentRound, (ulong)gapNumber);
        Signature signedHash = _signer.Sign(msgHash);
        var timeoutMsg = new Timeout(_consensusContext.CurrentRound, signedHash, (ulong)gapNumber);
        timeoutMsg.Signer = _signer.Address;

        HandleTimeoutVote(timeoutMsg);

        //TODO: Broadcast _ctx.HighestTC
    }

    // Returns true if the signer is within the master node list
    private bool AllowedToSend()
    {
        var currentHeader = (XdcBlockHeader)_blockTree.Head?.Header;
        EpochSwitchInfo epochSwitchInfo = _epochSwitchManager.GetEpochSwitchInfo(currentHeader);
        if (epochSwitchInfo is null)
            return false;
        return epochSwitchInfo.Masternodes.Contains(_signer.Address);
    }

    internal static ValueHash256 ComputeTimeoutMsgHash(ulong round, ulong gap)
    {
        Timeout timeout = new(round, null, gap);
        KeccakRlpStream stream = new KeccakRlpStream();
        _timeoutDecoder.Encode(stream, timeout, RlpBehaviors.ForSealing);
        return stream.GetValueHash();
    }

    public long GetTimeoutsCount(Timeout timeout)
    {
        return _timeouts.GetCount(timeout);
    }

}
