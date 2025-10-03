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

    public void ProcessTimeoutCertificate(TimeoutCertificate timeoutCertificate)
    {
        throw new NotImplementedException();
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

        var signatures = new HashSet<Signature>(timeoutCertificate.Signatures);
        BlockHeader header = _blockTree.Head?.Header;
        if (header is not XdcBlockHeader xdcHeader)
            throw new InvalidOperationException($"Only type of {nameof(XdcBlockHeader)} is allowed");
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

        ValueHash256 timeoutMsgHash = timeoutCertificate.SigHash();
        bool allValid = true;
        Parallel.ForEach(signatures,
            (signature, state) =>
            {
                Address signer = _ethereumEcdsa.RecoverAddress(signature, in timeoutMsgHash);
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
}

