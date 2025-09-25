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
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Xdc;
using Nethermind.Xdc.Errors;
using Nethermind.Xdc.Spec;
using Nethermind.Xdc.Types;
using static Nethermind.Core.BlockHeader;

namespace Nethermind.Xdc;
internal class QuorumCertificateManager : IQuorumCertificateManager
{
    public QuorumCertificateManager(
        XdcContext context,
        IBlockTree chain,
        IDb qcDb,
        ISpecProvider xdcConfig,
        IEpochSwitchManager epochSwitchManager)
    {
        _context = context;
        _blockTree = chain;
        _qcDb = qcDb;
        _specProvider = xdcConfig;
        _epochSwitchManager = epochSwitchManager;
    }

    private XdcContext _context { get; }
    private IBlockTree _blockTree;
    private readonly IDb _qcDb;
    private IEpochSwitchManager _epochSwitchManager { get; }
    private ISpecProvider _specProvider { get; }
    private EthereumEcdsa _ethereumEcdsa = new EthereumEcdsa(0);

    public void CommitCertificate(QuorumCertificate qc)
    {
        if (qc.ProposedBlockInfo.Round > _context.HighestQC.ProposedBlockInfo.Round)
        {
            _context.HighestQC = qc;
        }
        
        var proposedBlockHeader = (XdcBlockHeader)_blockTree.FindHeader(qc.ProposedBlockInfo.Hash);
        if (proposedBlockHeader is null)
            throw new InvalidBlockException(proposedBlockHeader, "Proposed block header not found in chain");

        //TODO this could be wrong way of fetching spec if a release spec is defined on a round basis 
        IXdcReleaseSpec spec = _specProvider.GetXdcSpec(proposedBlockHeader);

        //Can only look for a QC in proposed block after the switch block
        if (proposedBlockHeader.Number > spec.SwitchBlock)
        {
            QuorumCertificate? parentQc = proposedBlockHeader.ExtraConsensusData?.QuorumCert;
            if (parentQc is null)
            {
                throw new BlockchainException("QC is targeting a block without required consensus data.");
            }

            if (_context.LockQC is null || parentQc.ProposedBlockInfo.Round > _context.LockQC.ProposedBlockInfo.Round)
            {
                //Basically finalize parent QC
                _context.LockQC = parentQc;
            }

            CommitBlock(_blockTree, proposedBlockHeader, proposedBlockHeader.ExtraConsensusData.CurrentRound, qc);
        }

        if (qc.ProposedBlockInfo.Round >= _context.CurrentRound)
        {
            _context.SetNewRound(_blockTree, qc.ProposedBlockInfo.Round);
        }
    }

    private bool CommitBlock(IBlockTree chain, XdcBlockHeader proposedBlockHeader, ulong proposedRound, QuorumCertificate proposedQuorumCert)
    {
        IXdcReleaseSpec spec = _specProvider.GetXdcSpec(proposedBlockHeader);
        //Can only commit a QC if the proposed block is at least 2 blocks after the switch block, since we want to check grand parent of proposed QC
        if ((proposedBlockHeader.Number - 2) <= spec.SwitchBlock)
        {
            return false;
        }

        XdcBlockHeader parentHeader = (XdcBlockHeader)_blockTree.FindHeader(proposedBlockHeader.ParentHash);

        if (parentHeader.ExtraConsensusData is null)
            return false;

        if (proposedRound - 1 != parentHeader.ExtraConsensusData.CurrentRound)
        {
            throw new QuorumCertificateException(proposedQuorumCert, "QC round does not match parent QC round.");
        }

        XdcBlockHeader grandParentHeader = (XdcBlockHeader)_blockTree.FindHeader(parentHeader.ParentHash);

        if (grandParentHeader.ExtraConsensusData is null)
        {
            throw new QuorumCertificateException(proposedQuorumCert, "QC grand parent does not have a QC.");
        }

        if (proposedRound - 2 != parentHeader.ExtraConsensusData.CurrentRound)
        {
            throw new QuorumCertificateException(proposedQuorumCert, "QC round does not match grand parent QC round.");
        }

        if (_context.HighestCommitBlock is not null && (_context.HighestCommitBlock.Round >= parentHeader.ExtraConsensusData.CurrentRound || _context.HighestCommitBlock.BlockNumber > grandParentHeader.Number))
        {
            return false;
        }

        _context.HighestCommitBlock = new BlockRoundInfo(grandParentHeader.Hash, parentHeader.ExtraConsensusData.CurrentRound, grandParentHeader.Number);

        var headerQcToBeCommitted = new List<XdcBlockHeader> { parentHeader, proposedBlockHeader };

        return true;
    }

    public bool VerifyCertificate(QuorumCertificate qc, XdcBlockHeader parentHeader, out string error)
    {
        if (qc is null)
            throw new ArgumentNullException(nameof(qc));
        if (parentHeader is null)
            throw new ArgumentNullException(nameof(parentHeader));
        if (qc.Signatures is null)
            throw new ArgumentException("QC must contain vote signatures.", nameof(qc));

        EpochSwitchInfo epochSwitchInfo = _epochSwitchManager.GetEpochSwitchInfo(parentHeader, qc.ProposedBlockInfo.Hash);
        if (epochSwitchInfo is null)
        {
            error = $"Epoch switch info not found for header {parentHeader?.ToString(Format.FullHashAndNumber)}";
            return false;
        }

        //Possible optimize here
        Signature[] uniqueSignatures = qc.Signatures.Distinct().ToArray();

        IXdcReleaseSpec spec = _specProvider.GetXdcSpec(parentHeader);
        ulong qcRound = qc.ProposedBlockInfo.Round;
        double certThreshold = spec.Configs[qcRound].CertThreshold;

        if ((qcRound > 0) && (uniqueSignatures.Length < epochSwitchInfo.Masternodes.Length * certThreshold))
        {
            error = $"Number of votes ({uniqueSignatures.Length}) does not meet threshold of {certThreshold}";
            return false;
        }

        bool allValid = true;
        Parallel.ForEach(uniqueSignatures, new ParallelOptions() { MaxDegreeOfParallelism = Environment.ProcessorCount }, (s, state) =>
        {
            Address signer = _ethereumEcdsa.RecoverVoteSigner(new Vote(qc.ProposedBlockInfo, qc.GapNumber, s));
            if (!epochSwitchInfo.Masternodes.Contains(signer))
            {
                allValid = false;
                state.Break();
            }
        });

        if (!allValid)
        {
            error = $"Quorum certificate contains one or more invalid vote signatures";
            return false;
        }

        long epochSwitchNumber = epochSwitchInfo.EpochSwitchBlockInfo.BlockNumber;
        long gapNumber = epochSwitchNumber - (epochSwitchNumber % (long)spec.EpochLength) - (long)spec.Gap;

        if (epochSwitchNumber - (epochSwitchNumber % (long)spec.EpochLength) < (long)spec.Gap)
            gapNumber = 0;

        if (gapNumber != (long)qc.GapNumber)
        {
            error = $"Gap number mismatch between QC Gap {qc.GapNumber} and {gapNumber}";
            return false;
        }

        // Note : this method should be moved outside of Context
        //TODO verify block info matches the parent header
        if (!ValidateBlockInfo(qc, parentHeader))
        {
            error = "QC block data does not match header data.";
            return false;
        }

        error = null;
        return true;
    }

    private bool ValidateBlockInfo(QuorumCertificate qc, XdcBlockHeader parentHeader)
    {
        if (qc.ProposedBlockInfo.BlockNumber != parentHeader.Number)
            return false;
        if (qc.ProposedBlockInfo.Hash != parentHeader.Hash)
            return false;
        if (qc.ProposedBlockInfo.Round != parentHeader.ExtraConsensusData.CurrentRound)
            return false;
        return true;
    }
}
