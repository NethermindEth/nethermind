// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Xdc.Errors;
using Nethermind.Xdc.Spec;
using Nethermind.Xdc.Types;
using System;
using System.Diagnostics.CodeAnalysis;

namespace Nethermind.Xdc;

internal class QuorumCertificateManager(
    IXdcConsensusContext context,
    IBlockTree blockTree,
    ISpecProvider xdcConfig,
    IEpochSwitchManager epochSwitchManager,
    ILogManager logManager) : IQuorumCertificateManager
{
    private IXdcConsensusContext _context { get; } = context;
    private readonly IBlockTree _blockTree = blockTree;
    private IEpochSwitchManager _epochSwitchManager { get; } = epochSwitchManager;

    private ILogger _logger = logManager.GetClassLogger<QuorumCertificateManager>();

    private ISpecProvider _specProvider { get; } = xdcConfig;
    private static readonly VoteDecoder _voteDecoder = new();

    public QuorumCertificate HighestKnownCertificate => _context.HighestQC;
    public QuorumCertificate LockCertificate => _context.LockQC;

    public void CommitCertificate(QuorumCertificate qc)
    {
        if (qc.ProposedBlockInfo.Round > _context.HighestQC.ProposedBlockInfo.Round)
        {
            _context.HighestQC = qc;
        }

        XdcBlockHeader proposedBlockHeader = (XdcBlockHeader)_blockTree.FindHeader(qc.ProposedBlockInfo.Hash)
            ?? throw new IncomingMessageBlockNotFoundException(qc.ProposedBlockInfo.Hash, qc.ProposedBlockInfo.BlockNumber);

        IXdcReleaseSpec spec = _specProvider.GetXdcSpec(proposedBlockHeader, _context.CurrentRound);

        //Can only look for a QC in proposed block after the switch block
        if (proposedBlockHeader.Number > spec.SwitchBlock)
        {
            QuorumCertificate? parentQc = proposedBlockHeader.ExtraConsensusData?.QuorumCert
                ?? throw new BlockchainException("QC is targeting a block without required consensus data.");

            if (_context.LockQC is null || parentQc.ProposedBlockInfo.Round > _context.LockQC.ProposedBlockInfo.Round)
            {
                //Parent QC is now our lock
                _context.LockQC = parentQc;
            }

            if (!CommitBlock(proposedBlockHeader, proposedBlockHeader.ExtraConsensusData.BlockRound, qc, out string error))
            {
                if (_logger.IsWarn) _logger.Warn($"Could not commit block ({proposedBlockHeader.Hash}). {error}");
            }
        }

        if (qc.ProposedBlockInfo.Round >= _context.CurrentRound)
        {
            _context.SetNewRound(qc.ProposedBlockInfo.Round + 1);
        }
    }

    private bool CommitBlock(XdcBlockHeader proposedBlockHeader, ulong proposedRound, QuorumCertificate proposedQuorumCert, [NotNullWhen(false)] out string? error)
    {
        IXdcReleaseSpec spec = _specProvider.GetXdcSpec(proposedBlockHeader);
        //Can only commit a QC if the proposed block is at least 2 blocks after the switch block, since we want to check grandparent of proposed QC

        if ((proposedBlockHeader.Number - 2) <= spec.SwitchBlock)
        {
            error = $"Proposed block ({proposedBlockHeader.Number}) is too close or before genesis block ({spec.SwitchBlock})";
            return false;
        }

        if (_blockTree.FindHeader(proposedBlockHeader.ParentHash!) is not XdcBlockHeader parentHeader)
        {
            error = $"Parent header {proposedBlockHeader.ParentHash} is missing.";
            return false;
        }

        if (parentHeader.ExtraConsensusData is null)
        {
            error = $"Block {parentHeader.ToString(BlockHeader.Format.FullHashAndNumber)} does not have required consensus data! Chain might be corrupt!";
            return false;
        }

        if (proposedRound - 1 != parentHeader.ExtraConsensusData.BlockRound)
        {
            error = $"QC round is not continuous from parent QC round.";
            return false;
        }

        if (_blockTree.FindHeader(parentHeader.ParentHash!) is not XdcBlockHeader grandParentHeader)
        {
            error = $"Grandparent header {parentHeader.ParentHash} is missing.";
            return false;
        }

        if (grandParentHeader.ExtraConsensusData is null)
        {
            error = $"QC grand parent ({grandParentHeader.ToString(BlockHeader.Format.FullHashAndNumber)}) does not have a QC.";
            return false;
        }

        if (proposedRound - 2 != grandParentHeader.ExtraConsensusData.BlockRound)
        {
            error = $"QC round is not continuous from grand parent QC round.";
            return false;
        }

        //We will normally commit twice - once when QC vote finished and once when we receive new block containing the same QC most likely
        if (_context.HighestCommitBlock is not null && grandParentHeader.Hash == _context.HighestCommitBlock.Hash)
        {
            error = null;
            return true;
        }

        if (_context.HighestCommitBlock is not null
            && (_context.HighestCommitBlock.Round >= grandParentHeader.ExtraConsensusData.BlockRound || _context.HighestCommitBlock.BlockNumber > grandParentHeader.Number))
        {
            error = $"Committed block (round={_context.HighestCommitBlock.Round} #{_context.HighestCommitBlock.BlockNumber} hash={_context.HighestCommitBlock.Hash}) has higher round or block number than proposed header grandparent #{grandParentHeader.Number} round={grandParentHeader.ExtraConsensusData.BlockRound} hash={grandParentHeader.Hash}.";
            return false;
        }

        _context.HighestCommitBlock = new BlockRoundInfo(grandParentHeader.Hash, grandParentHeader.ExtraConsensusData.BlockRound, grandParentHeader.Number);
        _logger.Info($"Committed block {grandParentHeader.ToString(BlockHeader.Format.Short)} round={grandParentHeader.ExtraConsensusData.BlockRound}");
        //Mark grand parent as finalized
        _blockTree.ForkChoiceUpdated(grandParentHeader.Hash, grandParentHeader.Hash);
        error = null;
        return true;
    }

    public bool VerifyCertificate(QuorumCertificate qc, [NotNullWhen(false)] out string error)
    {
        XdcBlockHeader certificateTarget = (XdcBlockHeader)_blockTree.FindHeader(qc.ProposedBlockInfo.Hash);
        if (certificateTarget is null)
        {
            error = $"Certificate target block not found hash={qc.ProposedBlockInfo.Hash}";
            return false;
        }
        return VerifyCertificate(qc, certificateTarget, out error);
    }

    public bool VerifyCertificate(QuorumCertificate qc, XdcBlockHeader certificateTarget, [NotNullWhen(false)] out string error)
    {
        ArgumentNullException.ThrowIfNull(qc);
        ArgumentNullException.ThrowIfNull(certificateTarget);
        if (qc.Signatures is null)
            throw new ArgumentException("QC must contain vote signatures.", nameof(qc));

        EpochSwitchInfo epochSwitchInfo = _epochSwitchManager.GetEpochSwitchInfo(certificateTarget) ?? _epochSwitchManager.GetEpochSwitchInfo(qc.ProposedBlockInfo.Hash);
        if (epochSwitchInfo is null)
        {
            error = $"Epoch switch info not found for header {certificateTarget?.ToString(BlockHeader.Format.FullHashAndNumber)}";
            return false;
        }

        ulong qcRound = qc.ProposedBlockInfo.Round;
        IXdcReleaseSpec spec = _specProvider.GetXdcSpec(certificateTarget, qcRound);
        double certificateThreshold = spec.CertificateThreshold;
        double required = Math.Ceiling(epochSwitchInfo.Masternodes.Length * certificateThreshold);

        if (qcRound > 0)
        {
            (Address[] masternodes, Signature[] signatures) = (epochSwitchInfo.Masternodes, qc.Signatures);
            if (signatures.Length < required)
            {
                error = $"Number of signatures ({signatures.Length}) does not meet threshold of {required}";
                return false;
            }

            ValueHash256 voteHash = VoteHash(qc.ProposedBlockInfo, qc.GapNumber);
            if (VotesManager.CountValidSignatures(masternodes, signatures, voteHash, out error) is not { } signCount)
            {
                return false;
            }

            if (signCount < required)
            {
                error = $"Number of votes ({signCount}/{masternodes.Length}) does not meet threshold of {certificateThreshold}";
                return false;
            }
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

        if (certificateTarget.Number == spec.SwitchBlock)
        {
            //Do not check round info on genesis block
            if (qc.ProposedBlockInfo.BlockNumber != certificateTarget.Number || qc.ProposedBlockInfo.Hash != certificateTarget.Hash)
            {
                error = "QC genesis block data does not match header data.";
                return false;
            }
        }
        else if (!qc.ProposedBlockInfo.ValidateBlockInfo(certificateTarget))
        {
            error = "QC block data does not match header data.";
            return false;
        }

        error = null;
        return true;
    }

    private static ValueHash256 VoteHash(BlockRoundInfo proposedBlockInfo, ulong gapNumber)
    {
        KeccakRlpStream stream = new();
        _voteDecoder.Encode(stream, new Vote(proposedBlockInfo, gapNumber), RlpBehaviors.ForSealing);
        return stream.GetValueHash();
    }

    public void Initialize(XdcBlockHeader current)
    {
        IXdcReleaseSpec spec = _specProvider.GetXdcSpec(current);
        QuorumCertificate latestQc;
        if (current.Number == spec.SwitchBlock)
        {
            latestQc = new QuorumCertificate(new BlockRoundInfo(current.Hash, 0, current.Number), Array.Empty<Signature>(),
                    (ulong)Math.Max(0, current.Number - spec.Gap));
            _context.HighestQC = latestQc;
            _context.SetNewRound(1);
        }
        else
        {
            CommitCertificate(current.ExtraConsensusData.QuorumCert);
        }
    }
}
