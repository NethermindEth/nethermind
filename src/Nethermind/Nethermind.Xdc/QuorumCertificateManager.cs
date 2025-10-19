// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Microsoft.Extensions.Logging;
using Nethermind.Blockchain;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Xdc;
using Nethermind.Xdc.Errors;
using Nethermind.Xdc.Spec;
using Nethermind.Xdc.Types;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using static Nethermind.Core.BlockHeader;

namespace Nethermind.Xdc;
internal class QuorumCertificateManager : IQuorumCertificateManager
{
    public QuorumCertificateManager(
        IXdcConsensusContext context,
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

    private IXdcConsensusContext _context { get; }
    private IBlockTree _blockTree;
    private readonly IDb _qcDb;
    private readonly static VoteDecoder _voteDecoder = new();

    private IEpochSwitchManager _epochSwitchManager { get; }
    private ISpecProvider _specProvider { get; }
    private EthereumEcdsa _ethereumEcdsa = new EthereumEcdsa(0);
    private static QuorumCertificateDecoder QuorumCertificateDecoder = new();

    public QuorumCertificate HighestKnownCertificate => _context.HighestQC;
    public QuorumCertificate LockCertificate => _context.LockQC;

    public void CommitCertificate(QuorumCertificate qc)
    {
        if (qc.ProposedBlockInfo.Round > _context.HighestQC.ProposedBlockInfo.Round)
        {
            _context.HighestQC = qc;
            SaveHighestQc(qc);
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
                throw new BlockchainException("QC is targeting a block without required consensus data.");

            if (_context.LockQC is null || parentQc.ProposedBlockInfo.Round > _context.LockQC.ProposedBlockInfo.Round)
            {
                //Basically finalize parent QC
                _context.LockQC = parentQc;
                SaveLockQc(parentQc);
            }

            CommitBlock(_blockTree, proposedBlockHeader, proposedBlockHeader.ExtraConsensusData.BlockRound, qc);
        }
    }

    public bool VerifyVotingRule(XdcBlockHeader header)
    {
        if (_context.CurrentRound <= _context.HighestVotedRound ||
            header.ExtraConsensusData.BlockRound != _context.CurrentRound)
        {
            return false;
        }

        //TODO check this behavior again when transition from V1 to V2 is better defined
        if (_context.LockQC is null)
        {
            return true;
        }

        //Exception in the voting rule described in the whitepaper
        //https://xdcf.cdn.prismic.io/xdcf/876fd551-96c0-41e8-9a9a-437620cc1fee_XDPoS2.0_whitepaper.pdf
        if (_context.LockQC.ProposedBlockInfo.Round < header.ExtraConsensusData.QuorumCert.ProposedBlockInfo.Round)
        {
            return true;
        }

        //We can only vote for a QC that is an ancestor of our lock QC
        if (IsAncestor(_context.LockQC.ProposedBlockInfo, header.ExtraConsensusData.QuorumCert.ProposedBlockInfo))
        {
            return true;
        }

        return false;
    }

    private bool IsAncestor(BlockRoundInfo ancestor, BlockRoundInfo child)
    {
        long blockNumberDiff = child.BlockNumber - ancestor.BlockNumber;
        if (blockNumberDiff < 0)
            return false;
        BlockHeader parentHeader = _blockTree.FindHeader(child.Hash, child.BlockNumber);
        //TODO should this be bounded by some max number of blocks?
        for (int i = 0; i < blockNumberDiff; i++)
        {
            if (parentHeader is null)
                return false;
            if (parentHeader.Hash == ancestor.Hash)
                return true;
            parentHeader = _blockTree.FindHeader(parentHeader.ParentHash, parentHeader.Number - 1);
        }
        return false;
    }

    private void SaveHighestQc(QuorumCertificate qc)
    {
        SaveQc(qc, XdcDbNames.HighestQcKey);
    }
    private void SaveLockQc(QuorumCertificate qc)
    {
        SaveQc(qc, XdcDbNames.LockQcKey);
    }

    private void SaveQc(QuorumCertificate qc, long key)
    {
        byte[] data = new byte[QuorumCertificateDecoder.GetLength(qc)];
        RlpStream rlp = new RlpStream(data);
        QuorumCertificateDecoder.Encode(rlp, qc);
        _qcDb.Set(key, data);
    }

    private bool CommitBlock(IBlockTree chain, XdcBlockHeader proposedBlockHeader, ulong proposedRound, QuorumCertificate proposedQuorumCert)
    {
        IXdcReleaseSpec spec = _specProvider.GetXdcSpec(proposedBlockHeader);
        //Can only commit a QC if the proposed block is at least 2 blocks after the switch block, since we want to check grand parent of proposed QC
        if ((proposedBlockHeader.Number - 2) <= spec.SwitchBlock)
            return false;

        XdcBlockHeader parentHeader = (XdcBlockHeader)_blockTree.FindHeader(proposedBlockHeader.ParentHash);

        if (parentHeader.ExtraConsensusData is null)
            return false;

        if (proposedRound - 1 != parentHeader.ExtraConsensusData.BlockRound)
            throw new QuorumCertificateException(proposedQuorumCert, "QC round does not match parent QC round.");

        XdcBlockHeader grandParentHeader = (XdcBlockHeader)_blockTree.FindHeader(parentHeader.ParentHash);

        if (grandParentHeader.ExtraConsensusData is null)
            throw new QuorumCertificateException(proposedQuorumCert, "QC grand parent does not have a QC.");

        if (proposedRound - 2 != parentHeader.ExtraConsensusData.BlockRound)
            throw new QuorumCertificateException(proposedQuorumCert, "QC round does not match grand parent QC round.");

        if (_context.HighestCommitBlock is not null && (_context.HighestCommitBlock.Round >= parentHeader.ExtraConsensusData.BlockRound || _context.HighestCommitBlock.BlockNumber > grandParentHeader.Number))
            return false;

        _context.HighestCommitBlock = new BlockRoundInfo(grandParentHeader.Hash, parentHeader.ExtraConsensusData.BlockRound, grandParentHeader.Number);

        //Finalize the grand parent
        _blockTree.ForkChoiceUpdated(grandParentHeader.Hash, grandParentHeader.Hash);

        return true;
    }

    public bool VerifyCertificate(QuorumCertificate qc, XdcBlockHeader certificateTarget, out string error)
    {
        if (qc is null)
            throw new ArgumentNullException(nameof(qc));
        if (certificateTarget is null)
            throw new ArgumentNullException(nameof(certificateTarget));
        if (qc.Signatures is null)
            throw new ArgumentException("QC must contain vote signatures.", nameof(qc));

        EpochSwitchInfo epochSwitchInfo = _epochSwitchManager.GetEpochSwitchInfo(certificateTarget, qc.ProposedBlockInfo.Hash);
        if (epochSwitchInfo is null)
        {
            error = $"Epoch switch info not found for header {certificateTarget?.ToString(Format.FullHashAndNumber)}";
            return false;
        }

        //Possible optimize here
        Signature[] uniqueSignatures = qc.Signatures.Distinct().ToArray();

        ulong qcRound = qc.ProposedBlockInfo.Round;
        IXdcReleaseSpec spec = _specProvider.GetXdcSpec(certificateTarget, qcRound);
        double certThreshold = spec.CertThreshold;

        if ((qcRound > 0) && (uniqueSignatures.Length < epochSwitchInfo.Masternodes.Length * certThreshold))
        {
            error = $"Number of votes ({uniqueSignatures.Length}) does not meet threshold of {certThreshold}";
            return false;
        }

        ValueHash256 voteHash = VoteHash(qc.ProposedBlockInfo, qc.GapNumber);
        bool allValid = true;
        Parallel.ForEach(uniqueSignatures, (s, state) =>
        {
            Address signer = _ethereumEcdsa.RecoverAddress(s, voteHash);
            if (!epochSwitchInfo.Masternodes.Contains(signer))
            {
                allValid = false;
                state.Stop();
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

        if (!ValidateBlockInfo(qc, certificateTarget))
        {
            error = "QC block data does not match header data.";
            return false;
        }

        error = null;
        return true;
    }

    private ValueHash256 VoteHash(BlockRoundInfo proposedBlockInfo, ulong gapNumber)
    {
        KeccakRlpStream stream = new();
        _voteDecoder.Encode(stream, new Vote(proposedBlockInfo, gapNumber), RlpBehaviors.ForSealing);
        return stream.GetValueHash();
    }

    private bool ValidateBlockInfo(QuorumCertificate qc, XdcBlockHeader parentHeader)
    {
        if (qc.ProposedBlockInfo.BlockNumber != parentHeader.Number)
            return false;
        if (qc.ProposedBlockInfo.Hash != parentHeader.Hash)
            return false;
        if (qc.ProposedBlockInfo.Round != parentHeader.ExtraConsensusData.BlockRound)
            return false;
        return true;
    }
}
