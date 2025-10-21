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

public class TimeoutCertificateManager(XdcContext context, ISnapshotManager snapshotManager, IEpochSwitchManager epochSwitchManager, ISpecProvider specProvider, IBlockTree blockTree, ISyncInfoManager syncInfoManager, ISigner signer) : ITimeoutCertificateManager
{
    private XdcContext _ctx = context;
    private ISnapshotManager _snapshotManager = snapshotManager;
    private IEpochSwitchManager _epochSwitchManager = epochSwitchManager;
    private ISpecProvider _specProvider = specProvider;
    private IBlockTree _blockTree = blockTree;
    private ISyncInfoManager _syncInfoManager = syncInfoManager;
    private ISigner _signer = signer;

    private EthereumEcdsa _ethereumEcdsa = new EthereumEcdsa(0);
    private static readonly TimeoutDecoder _timeoutDecoder = new();
    private XdcPool<Timeout> _timeouts = new();

    public Task HandleTimeout(Timeout timeout)
    {
        if (timeout.Round != _ctx.CurrentRound)
        {
            // Not interested in processing timeout for round different from the current one
            return Task.CompletedTask;
        }

        var count = _timeouts.Add(timeout);
        var collectedTimeouts = _timeouts.GetItems(timeout);

        var xdcHeader = _blockTree.Head?.Header as XdcBlockHeader;
        EpochSwitchInfo epochSwitchInfo = _epochSwitchManager.GetEpochSwitchInfo(xdcHeader, xdcHeader.Hash);
        if (epochSwitchInfo is null)
        {
            // Failed to get epoch switch info, cannot process timeout
            return Task.CompletedTask;
        }

        IXdcReleaseSpec spec = _specProvider.GetXdcSpec(xdcHeader, timeout.Round);
        var certThreshold = spec.CertThreshold;
        if (count >= epochSwitchInfo.Masternodes.Length * certThreshold)
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
        if (timeoutCertificate.Round > _ctx.HighestTC.Round)
        {
            _ctx.HighestTC = timeoutCertificate;
        }

        if (timeoutCertificate.Round >= _ctx.CurrentRound)
        {
            //TODO Check how this new round is set
            _ctx.SetNewRound(_blockTree, timeoutCertificate.Round + 1);
        }
    }

    public bool VerifyTimeoutCertificate(TimeoutCertificate timeoutCertificate, out string errorMessage)
    {
        if (timeoutCertificate is null) throw new ArgumentNullException(nameof(timeoutCertificate));
        if (timeoutCertificate.Signatures is null) throw new ArgumentNullException(nameof(timeoutCertificate.Signatures));

        Snapshot snapshot = _snapshotManager.GetSnapshotByGapNumber(_blockTree, timeoutCertificate.GapNumber);
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
        var xdcHeader = _blockTree.Head?.Header as XdcBlockHeader;
        IXdcReleaseSpec spec = _specProvider.GetXdcSpec(xdcHeader, timeoutCertificate.Round);
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
        _ctx.TimeoutCounter++;

        var xdcHeader = _blockTree.Head?.Header as XdcBlockHeader;
        IXdcReleaseSpec spec = _specProvider.GetXdcSpec(xdcHeader!, _ctx.CurrentRound);

        if (_ctx.TimeoutCounter % spec.TimeoutSyncThreshold == 0)
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
        var epochLenth = _specProvider.GetXdcSpec(currentHeader, timeout.Round).EpochLength;
        if (Math.Abs((long)timeout.GapNumber - currentBlockNumber) > 3 * epochLenth)
        {
            // Discarded propagated timeout, too far away
            return Task.CompletedTask;
        }

        if (FilterTimeout(timeout))
        {
            //TODO: Broadcast Timeout
            return HandleTimeout(timeout);
        }
        return Task.CompletedTask;
    }

    private bool FilterTimeout(Timeout timeout)
    {
        if (timeout.Round < _ctx.CurrentRound) return false;
        Snapshot snapshot = _snapshotManager.GetSnapshotByGapNumber(_blockTree, timeout.GapNumber);
        if (snapshot is null || snapshot.NextEpochCandidates.Length == 0) return false;

        // Verify msg signature
        ValueHash256 timeoutMsgHash = ComputeTimeoutMsgHash(timeout.Round, timeout.GapNumber);
        Address signer = _ethereumEcdsa.RecoverAddress(timeout.Signature, in timeoutMsgHash);
        timeout.Signer = signer;

        return snapshot.NextEpochCandidates.Contains(signer);
    }

    private void SendTimeout()
    {
        ulong gapNumber = 0;
        var currentHeader = (XdcBlockHeader)_blockTree.Head?.Header;
        if (currentHeader is null) throw new InvalidOperationException("Failed to retrieve current header");
        IXdcReleaseSpec spec = _specProvider.GetXdcSpec(currentHeader, _ctx.CurrentRound);
        if (_epochSwitchManager.IsEpochSwitchAtRound(_ctx.CurrentRound, currentHeader, out ulong epochNumber))
        {
            ulong currentNumber = (ulong)currentHeader.Number + 1;
            gapNumber = Math.Max(0, currentNumber - currentNumber % (ulong)spec.EpochLength - (ulong)spec.Gap);
        }
        else
        {
            EpochSwitchInfo epochSwitchInfo = _epochSwitchManager.GetEpochSwitchInfo(currentHeader, currentHeader.Hash);
            if (epochSwitchInfo is null)
                throw new ConsensusHeaderDataExtractionException(nameof(EpochSwitchInfo));

            ulong currentNumber = (ulong)epochSwitchInfo.EpochSwitchBlockInfo.BlockNumber;
            gapNumber = Math.Max(0, currentNumber - currentNumber % (ulong)spec.EpochLength - (ulong)spec.Gap);
        }

        ValueHash256 msgHash = ComputeTimeoutMsgHash(_ctx.CurrentRound, gapNumber);
        Signature signedHash = _signer.Sign(msgHash);
        var timeoutMsg = new Timeout(_ctx.CurrentRound, signedHash, gapNumber);
        timeoutMsg.Signer = _signer.Address;

        HandleTimeout(timeoutMsg);

        //TODO: Broadcast _ctx.HighestTC
    }

    // Returns true if the signer is within the master node list
    private bool AllowedToSend()
    {
        var currentHeader = (XdcBlockHeader)_blockTree.Head?.Header;
        EpochSwitchInfo epochSwitchInfo = _epochSwitchManager.GetEpochSwitchInfo(currentHeader, currentHeader.Hash);
        if (epochSwitchInfo is null)
            return false;
        return epochSwitchInfo.Masternodes.Any(x => x == _signer.Address);
    }

    internal static ValueHash256 ComputeTimeoutMsgHash(ulong round, ulong gap)
    {
        var timeout = new Timeout(round, null, gap);
        Rlp encoded = _timeoutDecoder.Encode(timeout, RlpBehaviors.ForSealing);
        return Keccak.Compute(encoded.Bytes).ValueHash256;
    }

}
