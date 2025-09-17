// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;
using Nethermind.Blockchain;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Xdc;
using Nethermind.Xdc.Errors;
using Nethermind.Xdc.Spec;
using Nethermind.Xdc.Types;
using BlockInfo = Nethermind.Xdc.Types.BlockInfo;

namespace Nethermind.Xdc;
internal class QuorumCertificateManager : IQuorumCertificateManager
{
    public QuorumCertificateManager(
        XdcContext context,
        IBlockTree chain,
        IXdcReleaseSpec xdcConfig,
        IEpochSwitchManager epochSwitchManager)
    {
        _context = context;
        _blockTree = chain;
        _config = xdcConfig;
        _epochSwitchManager = epochSwitchManager;
    }

    private XdcContext _context { get; }
    private IBlockTree _blockTree;
    private IXdcReleaseSpec _config { get; }
    private IEpochSwitchManager _epochSwitchManager { get; }
    private EthereumEcdsa _ethereumEcdsa = new EthereumEcdsa(0);

    public void CommitCertificate(QuorumCert qc)
    {
        if (qc.ProposedBlockInfo.Round > _context.HighestQC.ProposedBlockInfo.Round)
        {
            _context.HighestQC = qc;
        }

        var proposedBlockHeader = (XdcBlockHeader)_blockTree.FindHeader(qc.ProposedBlockInfo.Hash);
        if (proposedBlockHeader is null)
        {
            throw new InvalidBlockException(proposedBlockHeader, "Proposed block header not found in chain");
        }

        if (proposedBlockHeader.Number > 0)
        {
            if (!Utils.TryGetExtraFields(proposedBlockHeader, (long)_config.SwitchBlock, out QuorumCert proposedQc, out ulong round, out _))
            {
                throw new ConsensusHeaderDataExtractionException(nameof(ExtraFieldsV2));
            }

            if (_context.LockQC is null || proposedQc.ProposedBlockInfo.Round > _context.LockQC.ProposedBlockInfo.Round)
            {
                _context.LockQC = proposedQc;
            }

            CommitBlock(_blockTree, proposedBlockHeader, round, qc);
        }

        if (qc.ProposedBlockInfo.Round >= _context.CurrentRound)
        {
            _context.SetNewRound(_blockTree, qc.ProposedBlockInfo.Round);
        }
    }

    private bool CommitBlock(IBlockTree chain, XdcBlockHeader proposedBlockHeader, ulong proposedRound, QuorumCert proposedQuorumCert)
    {
        if ((proposedBlockHeader.Number - 2) <= _config.SwitchBlock)
        {
            return false;
        }

        XdcBlockHeader parentHeader = (XdcBlockHeader)_blockTree.FindHeader(proposedBlockHeader.ParentHash);

        if (!Utils.TryGetExtraFields(parentHeader, (long)_config.SwitchBlock, out _, out ulong round, out _))
        {
            return false;
        }

        if (proposedRound - 1 != round)
        {
            return false;
        }

        XdcBlockHeader grandParentHeader = (XdcBlockHeader)_blockTree.FindHeader(parentHeader.ParentHash);

        if (!Utils.TryGetExtraFields(grandParentHeader, (long)_config.SwitchBlock, out _, out round, out _))
        {
            return false;
        }

        if (proposedRound - 2 != round)
        {
            return false;
        }

        if (_context.HighestCommitBlock is not null && (_context.HighestCommitBlock.Round >= round || _context.HighestCommitBlock.Number > grandParentHeader.Number))
        {
            return false;
        }

        _context.HighestCommitBlock = new BlockInfo(grandParentHeader.Hash, round, grandParentHeader.Number);

        var headerQcToBeCommitted = new List<XdcBlockHeader> { parentHeader, proposedBlockHeader };

        return true;
    }

    public bool VerifyCertificate(QuorumCert qc, XdcBlockHeader parentHeader, out string error)
    {
        if (qc is null)
            throw new ArgumentNullException(nameof(qc));
        if (parentHeader is null)
            throw new ArgumentNullException(nameof(parentHeader));

        if (!_epochSwitchManager.TryGetEpochSwitchInfo(parentHeader, qc.ProposedBlockInfo.Hash, out EpochSwitchInfo epochSwitchInfo))
        {
            throw new ConsensusHeaderDataExtractionException(nameof(EpochSwitchInfo));
        }

        Signature[] uniqueSignatures = qc.Signatures.Distinct().ToArray();

        ulong qcRound = qc.ProposedBlockInfo.Round;
        double certThreshold = _config.Configs[qcRound].CertThreshold;

        if ((qcRound > 0) && (uniqueSignatures is null || uniqueSignatures.Length < epochSwitchInfo.Masternodes.Length * certThreshold))
        {
            throw new CertificateValidationException(CertificateType.QuorumCertificate, CertificateValidationFailure.InvalidSignatures);
        }

        bool allValid = true;
        CancellationTokenSource cts = new();
        Parallel.ForEach(uniqueSignatures, new ParallelOptions() { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = cts.Token }, (s) =>
        {
            Address signer = _ethereumEcdsa.RecoverVoteSigner(new Vote(qc.ProposedBlockInfo, s, qc.GapNumber));
            if (!epochSwitchInfo.Masternodes.Contains(signer))
            {
                allValid = false;
                cts.Cancel();
            }
        });

        if (!allValid)
        {
            error = "Invalid vote signature";
            return false;
        }

        long epochSwitchNumber = epochSwitchInfo.EpochSwitchBlockInfo.Number;
        long gapNumber = epochSwitchNumber - (epochSwitchNumber % (long)_config.EpochLength) - (long)_config.Gap;

        if (epochSwitchNumber - (epochSwitchNumber % (long)_config.EpochLength) < (long)_config.Gap)
            gapNumber = 0;

        if (gapNumber != (long)qc.GapNumber)
        {
            error = $"Gap number mismatch between QC Gap {qc.GapNumber} and {gapNumber}";
            return false;
        }

        // Note : this method should be moved outside of Context
        //TODO verify block info matches the parent header

        error = null;
        return true;
    }
}
