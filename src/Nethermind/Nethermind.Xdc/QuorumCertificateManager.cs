// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Threading.Tasks;

using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Serialization.Rlp;
using Nethermind.Xdc.Errors;
using Nethermind.Xdc.Spec;
using Nethermind.Xdc.Types;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Logging;

namespace Nethermind.Xdc;
internal class QuorumCertificateManager : IQuorumCertificateManager
{
    public QuorumCertificateManager(
        IXdcConsensusContext context,
        IBlockTree blockTree,
        ISpecProvider xdcConfig,
        IEpochSwitchManager epochSwitchManager,
        ILogManager logManager)
    {
        _context = context;
        _blockTree = blockTree;
        _specProvider = xdcConfig;
        _epochSwitchManager = epochSwitchManager;
        _logger = logManager.GetClassLogger<QuorumCertificateManager>();
    }

    private IXdcConsensusContext _context { get; }
    private IBlockTree _blockTree;
    private IEpochSwitchManager _epochSwitchManager { get; }

    private ILogger _logger;

    private ISpecProvider _specProvider { get; }
    private EthereumEcdsa _ethereumEcdsa = new EthereumEcdsa(0);
    private readonly static VoteDecoder _voteDecoder = new();

    public QuorumCertificate HighestKnownCertificate => _context.HighestQC;
    public QuorumCertificate LockCertificate => _context.LockQC;

    public void CommitCertificate(QuorumCertificate qc)
    {
        if (qc.ProposedBlockInfo.Round > _context.HighestQC.ProposedBlockInfo.Round)
        {
            _context.HighestQC = qc;
        }

        var proposedBlockHeader = (XdcBlockHeader)_blockTree.FindHeader(qc.ProposedBlockInfo.Hash);
        if (proposedBlockHeader is null)
            throw new InvalidBlockException(proposedBlockHeader, "Proposed block header not found in chain");

        IXdcReleaseSpec spec = _specProvider.GetXdcSpec(proposedBlockHeader, _context.CurrentRound);

        //Can only look for a QC in proposed block after the switch block
        if (proposedBlockHeader.Number > spec.SwitchBlock)
        {
            QuorumCertificate? parentQc = proposedBlockHeader.ExtraConsensusData?.QuorumCert;
            if (parentQc is null)
                throw new BlockchainException("QC is targeting a block without required consensus data.");

            if (_context.LockQC is null || parentQc.ProposedBlockInfo.Round > _context.LockQC.ProposedBlockInfo.Round)
            {
                //Parent QC is now our lock
                _context.LockQC = parentQc;
            }

            if (!CommitBlock(_blockTree, proposedBlockHeader, proposedBlockHeader.ExtraConsensusData.BlockRound, qc, out string error))
            {
                if (_logger.IsWarn) _logger.Warn($"Could not commit block ({proposedBlockHeader.Hash}). {error}");
            }

        }

        if (qc.ProposedBlockInfo.Round >= _context.CurrentRound)
        {
            _context.SetNewRound(qc.ProposedBlockInfo.Round + 1);
        }
    }

    private bool CommitBlock(IBlockTree chain, XdcBlockHeader proposedBlockHeader, ulong proposedRound, QuorumCertificate proposedQuorumCert, [NotNullWhen(false)] out string? error)
    {
        IXdcReleaseSpec spec = _specProvider.GetXdcSpec(proposedBlockHeader);
        //Can only commit a QC if the proposed block is at least 2 blocks after the switch block, since we want to check grandparent of proposed QC

        if ((proposedBlockHeader.Number - 2) <= spec.SwitchBlock)
        {
            error = $"Proposed block ({proposedBlockHeader.Number}) is too close or before genesis block ({spec.SwitchBlock})";
            return false;
        }

        XdcBlockHeader parentHeader = (XdcBlockHeader)_blockTree.FindHeader(proposedBlockHeader.ParentHash);

        if (parentHeader.ExtraConsensusData is null)
        {
            error = $"Block {parentHeader.ToString(BlockHeader.Format.FullHashAndNumber)} does not have required consensus data! Chain migth be corrupt!";
            return false;
        }

        if (proposedRound - 1 != parentHeader.ExtraConsensusData.BlockRound)
        {
            error = $"QC round is not continuous from parent QC round.";
            return false;
        }

        XdcBlockHeader grandParentHeader = (XdcBlockHeader)_blockTree.FindHeader(parentHeader.ParentHash);

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

        if (_context.HighestCommitBlock is not null && (_context.HighestCommitBlock.Round >= parentHeader.ExtraConsensusData.BlockRound || _context.HighestCommitBlock.BlockNumber > grandParentHeader.Number))
        {

            error = $"Committed block ({_context.HighestCommitBlock.Hash}) has higher round or block number.";
            return false;
        }

        _context.HighestCommitBlock = new BlockRoundInfo(grandParentHeader.Hash, parentHeader.ExtraConsensusData.BlockRound, grandParentHeader.Number);

        //Mark grand parent as finalized
        _blockTree.ForkChoiceUpdated(grandParentHeader.Hash, grandParentHeader.Hash);
        error = null;
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

        EpochSwitchInfo epochSwitchInfo = _epochSwitchManager.GetEpochSwitchInfo(certificateTarget) ?? _epochSwitchManager.GetEpochSwitchInfo(qc.ProposedBlockInfo.Hash);
        if (epochSwitchInfo is null)
        {
            error = $"Epoch switch info not found for header {certificateTarget?.ToString(BlockHeader.Format.FullHashAndNumber)}";
            return false;
        }

        //Possible optimize here
        Signature[] uniqueSignatures = qc.Signatures.Distinct().ToArray();

        ulong qcRound = qc.ProposedBlockInfo.Round;
        IXdcReleaseSpec spec = _specProvider.GetXdcSpec(certificateTarget, qcRound);
        double certThreshold = spec.CertThreshold;
        double required = Math.Ceiling(epochSwitchInfo.Masternodes.Length * certThreshold);
        if ((qcRound > 0) && (uniqueSignatures.Length < required))
        {
            error = $"Number of votes ({uniqueSignatures.Length}/{epochSwitchInfo.Masternodes.Length}) does not meet threshold of {certThreshold}";
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
    private ValueHash256 VoteHash(BlockRoundInfo proposedBlockInfo, ulong gapNumber)
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
