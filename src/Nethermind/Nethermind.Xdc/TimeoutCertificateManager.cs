// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Xdc.Types;
using Nethermind.Xdc.Spec;

namespace Nethermind.Xdc;

public class TimeoutCertificateManager(ISnapshotManager snapshotManager, IEpochSwitchManager epochSwitchManager, ISpecProvider specProvider, IBlockTree blockTree) : ITimeoutCertificateManager
{
    private ISnapshotManager _snapshotManager = snapshotManager;
    private IEpochSwitchManager _epochSwitchManager = epochSwitchManager;
    private ISpecProvider _specProvider = specProvider;
    private IBlockTree _blockTree = blockTree;
    private EthereumEcdsa _ethereumEcdsa = new EthereumEcdsa(0);

    public void HandleTimeout(Timeout timeout)
    {
        throw new NotImplementedException();
    }

    public void OnCountdownTimer(DateTime time)
    {
        throw new NotImplementedException();
    }

    public void ProcessTimeoutCertificate(TimeoutCert timeoutCert)
    {
        throw new NotImplementedException();
    }

    public bool VerifyTimeoutCertificate(TimeoutCert timeoutCert, out string errorMessage)
    {
        if (timeoutCert is null) throw new ArgumentNullException(nameof(timeoutCert));
        if (timeoutCert.Signatures is null) throw new ArgumentNullException(nameof(timeoutCert.Signatures));

        bool ok = _snapshotManager.TryGetSnapshot(timeoutCert.GapNumber, true, out Snapshot snapshot);
        if (!ok || snapshot is null)
        {
            errorMessage = "Failed to get snapshot";
            return false;
        }

        if (snapshot.NextEpochCandidates.Length == 0)
        {
            errorMessage = "Empty master node lists from snapshot";
            return false;
        }

        var signatures = new HashSet<Signature>(timeoutCert.Signatures);
        BlockHeader header = _blockTree.Head?.Header;
        if (header is not XdcBlockHeader xdcHeader)
            throw new ArgumentException($"Only type of {nameof(XdcBlockHeader)} is allowed");
        IXdcReleaseSpec spec = _specProvider.GetXdcSpec(xdcHeader, timeoutCert.Round);
        EpochSwitchInfo epochInfo = GetTCEpochInfo(timeoutCert, xdcHeader, spec);
        if (signatures.Count < epochInfo.Masternodes.Length * spec.CertThreshold)
        {
            errorMessage = "Invalid TC Signatures";
            return false;
        }

        ValueHash256 signedTimeoutObj = new TimeoutForSign(timeoutCert.Round, timeoutCert.GapNumber).SigHash().ValueHash256;
        bool allValid = true;
        Parallel.ForEach(signatures,
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
            (signature, state) =>
            {
                Address signer = _ethereumEcdsa.RecoverAddress(signature, in signedTimeoutObj);
                if (!snapshot.NextEpochCandidates.Contains(signer))
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

    private EpochSwitchInfo GetTCEpochInfo(TimeoutCert timeoutCert, XdcBlockHeader xdcHeader, IXdcReleaseSpec spec)
    {

        EpochSwitchInfo epochSwitchInfo = _epochSwitchManager.GetEpochSwitchInfo(xdcHeader, xdcHeader.Hash);
        if (epochSwitchInfo is null) throw new Exception("Failed to get epoch switch info");
        var epochRound = epochSwitchInfo.EpochSwitchBlockInfo.Round;
        var tcEpoch = (ulong)spec.SwitchEpoch + epochRound / (ulong)spec.EpochLength;

        var epochBlockInfo = new BlockRoundInfo(epochSwitchInfo.EpochSwitchBlockInfo.Hash, epochRound,
            epochSwitchInfo.EpochSwitchBlockInfo.BlockNumber);

        // Reference: https://github.com/XinFinOrg/XDPoSChain/blob/af4178b2c7f9d668d8ba1f3a0244606a20ce303d/consensus/XDPoS/engines/engine_v2/timeout.go#L99
        while (epochBlockInfo.Round > timeoutCert.Round)
        {
            tcEpoch--;
            epochBlockInfo = _epochSwitchManager.GetBlockByEpochNumber(tcEpoch);
            if (epochBlockInfo is null) throw new Exception($"Failed to get block info for epoch={tcEpoch}");
        }

        EpochSwitchInfo epochInfo = _epochSwitchManager.GetEpochSwitchInfo(null, epochBlockInfo.Hash);
        if (epochInfo is null) throw new Exception("Failed to get epoch switch info");
        return epochInfo;
    }
}

