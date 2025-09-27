// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm.Tracing;
using Nethermind.Xdc;
using Nethermind.Xdc.Errors;
using Nethermind.Xdc.Types;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nethermind.Xdc;
internal class TimeoutCertificateManager : ITimeoutCertificateManager
{
    public TimeoutCertificateManager(
        XdcContext context,
        IBlockTree tree,
        IXdcConfig config,
        ISnapshotManager snapshotManager,
        ISignatureManager signatureManager,
        ISyncInfoManager syncInfoManager,
        IMasternodesManager masternodesManager,
        IEpochSwitchManager epochSwitchManager)
    {
        Context = context;
        Tree = tree;
        Config = config;
        SnapshotManager = snapshotManager;
        SignatureManager = signatureManager;
        SyncInfoManager = syncInfoManager;
        MasternodesManager = masternodesManager;
        EpochSwitchManager = epochSwitchManager;
    }

    public XdcContext Context { get; }
    public IBlockTree Tree { get; }
    public IXdcConfig Config { get; }
    public ISnapshotManager SnapshotManager { get; }
    public ISignatureManager SignatureManager { get; }
    public ISyncInfoManager SyncInfoManager { get; }
    public IEpochSwitchManager EpochSwitchManager { get; }
    public IMasternodesManager MasternodesManager { get; }
    public List<Timeout> _timeouts { get; set; }

    // broadcast tp BFT channel
    public event Action<SyncInfo> OnSyncInfoBroadcast;
    public event Action<TimeoutCert> OnTimeoutCertificateBroadcast;

    public void HandleTimeout(Timeout timeout)
    {
        if (timeout.Round != Context.CurrentRound)
        {
            throw new CertificateValidationException(CertificateType.TimeoutCertificate, CertificateValidationFailure.InvalidRound);
        }

        var headOfChain = (XdcBlockHeader)Tree.Head.Header;
        if (!EpochSwitchManager.TryGetEpochSwitchInfo(headOfChain, headOfChain.Hash, out EpochSwitchInfo epochInfo))
        {
            throw new ConsensusHeaderDataExtractionException(nameof(EpochSwitchInfo));
        }

        var certThreshold = Config.Configs[timeout.Round].CertThreshold;
        if (_timeouts.Count >= epochInfo.Masternodes.Length * certThreshold)
        {
            onTimeoutPoolThresholdReached(_timeouts, timeout, timeout.GapNumber);
        }
    }

    private void onTimeoutPoolThresholdReached(List<Timeout> timeouts, Timeout timeout, ulong gapNumber)
    {
        Signature[] signatures = new Signature[timeouts.Count];

        for (int i = 0; i < signatures.Length; i++)
        {
            signatures[i] = timeouts[i].Signature;
        }

        var timeoutCert = new TimeoutCert(timeout.Round, signatures, gapNumber);

        ProcessTimeoutCertificate(timeoutCert);

        var syncInfo = SyncInfoManager.GetSyncInfo();

        OnSyncInfoBroadcast?.Invoke(syncInfo);
    }

    public void VerifyTimeoutCertificate(TimeoutCert timeoutCert)
    {
        if (timeoutCert is null || timeoutCert.Signatures is null)
        {
            throw new CertificateValidationException(CertificateType.TimeoutCertificate, CertificateValidationFailure.InvalidContent);
        }

        if (!SnapshotManager.TryGetSnapshot(timeoutCert.GapNumber, isGapNumber: true, out Snapshot snap) || snap is null)
        {
            throw new Exception("Fail to get snapshot when verifying TC");
        }

        if (snap.NextEpochCandidates.Length == 0)
        {
            throw new Exception("Empty masternodes list from snapshot");
        }

        var filteredSignatures = Utils.FilterSignatures(timeoutCert.Signatures);

        if (!EpochSwitchManager.TryGetTimeoutCertificateEpochInfo(timeoutCert, out EpochSwitchInfo epochInfo))
        {
            throw new Exception("fail on getTCEpochInfo");
        }

        double certThreshold = Config.Configs[timeoutCert.Round].CertThreshold;

        if (filteredSignatures.Unique.Count < epochInfo.Masternodes.Length * certThreshold)
        {
            throw new CertificateValidationException(CertificateType.TimeoutCertificate, CertificateValidationFailure.InvalidSignatures);
        }

        var signedTimeoutObj = new TimeoutForSign(timeoutCert.Round, timeoutCert.GapNumber).SigHash();

        try
        {
            // Launch one Task per signature
            var tasks = new List<Task>();
            foreach (var signature in filteredSignatures.Unique)
            {
                tasks.Add(Task.Run(() =>
                {
                    if (!SignatureManager.VerifyMessageSignature(signedTimeoutObj, signature, snap.NextEpochCandidates, out Address _))
                    {
                        throw new CertificateValidationException(CertificateType.TimeoutCertificate, CertificateValidationFailure.InvalidSignatures);
                    }
                }));

                Task.WaitAll(tasks);
            }
        }
        catch
        {
            throw;
        }
    }

    public void ProcessTimeoutCertificate(TimeoutCert timeoutCert)
    {
        if (timeoutCert.Round > Context.HighestTC.Round)
        {
            Context.HighestTC = timeoutCert;
        }

        if (timeoutCert.Round >= Context.CurrentRound)
        {
            Context.SetNewRound(Tree, timeoutCert.Round + 1);
        }
    }

    private void SendTimeout()
    {
        XdcBlockHeader currentHeader = (XdcBlockHeader)Tree.Head.Header;

        ulong gapNumber = 0;
        if (EpochSwitchManager.IsEpochSwitchAtRound(Context.CurrentRound, currentHeader, out ulong epochNumber))
        {
            ulong currentNUmber = (ulong)currentHeader.Number + 1;
            gapNumber = Math.Max(0, currentNUmber - currentNUmber % Config.Epoch - Config.Gap);
        }
        else
        {
            if (!EpochSwitchManager.TryGetEpochSwitchInfo(currentHeader, currentHeader.Hash, out EpochSwitchInfo epochSwitchInfo))
            {
                throw new ConsensusHeaderDataExtractionException(nameof(EpochSwitchInfo));
            }

            ulong currentNUmber = (ulong)epochSwitchInfo.EpochSwitchBlockInfo.Number;
            gapNumber = Math.Max(0, currentNUmber - currentNUmber % Config.Epoch - Config.Gap);
        }

        var signedHash = SignatureManager.CurrentSigner.Sign(new TimeoutForSign(Context.CurrentRound, gapNumber).SigHash());
        var timeoutMsg = new Timeout(Context.CurrentRound, signedHash, gapNumber);

        timeoutMsg.SetSigner(SignatureManager.CurrentSigner.Address);

        HandleTimeout(timeoutMsg);

        OnTimeoutCertificateBroadcast?.Invoke(Context.HighestTC);
    }

    public void OnCountdownTimer(DateTime time)
    {
        if (!Utils.IsAllowedToSend(MasternodesManager, (XdcBlockHeader)Tree.Head.Header, SignatureManager.CurrentSigner.Address))
        {
            return;
        }

        SendTimeout();

        Context.TimeoutCounter++;

        if (Context.TimeoutCounter % Config.CurrentConfig.TimeoutSyncThreshold == 0)
        {
            var syncInfo = SyncInfoManager.GetSyncInfo();

            OnSyncInfoBroadcast?.Invoke(syncInfo);
        }
    }
}
