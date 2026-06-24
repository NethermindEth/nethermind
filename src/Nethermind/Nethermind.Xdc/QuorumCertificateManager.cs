// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Xdc.Errors;
using Nethermind.Xdc.Spec;
using Nethermind.Xdc.Types;
using System;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Xdc.RLP;

namespace Nethermind.Xdc;

internal class QuorumCertificateManager : IQuorumCertificateManager, IDisposable
{
    private readonly IXdcConsensusContext _context;
    private readonly IBlockTree _blockTree;
    private readonly IEpochSwitchManager _epochSwitchManager;
    private readonly IForensicsProcessor _forensicsProcessor;
    private readonly ILogger _logger;
    private readonly ISpecProvider _specProvider;
    private readonly object _commitLock = new();
    private static readonly VoteDecoder _voteDecoder = new();

    public QuorumCertificateManager(
        IXdcConsensusContext context,
        IBlockTree blockTree,
        ISpecProvider xdcConfig,
        IEpochSwitchManager epochSwitchManager,
        ILogManager logManager,
        IForensicsProcessor forensicsProcessor)
    {
        _context = context;
        _blockTree = blockTree;
        _specProvider = xdcConfig;
        _epochSwitchManager = epochSwitchManager;
        _forensicsProcessor = forensicsProcessor;
        _logger = logManager.GetClassLogger<QuorumCertificateManager>();

        _blockTree.OnUpdateMainChain += OnUpdateMainChain;

        BlockHeader? head = _blockTree.Head?.Header;
        if (head is not null)
        {
            if (head is not XdcBlockHeader xdcHead)
                throw new InvalidOperationException($"Expected an XDC header for chain head, but got {head.GetType().FullName}");
            Initialize(xdcHead);
        }
    }

    public QuorumCertificate HighestKnownCertificate => _context.HighestQC;
    public QuorumCertificate LockCertificate => _context.LockQC;

    public void CommitCertificate(QuorumCertificate qc)
    {
        XdcBlockHeader proposedBlockHeader = (XdcBlockHeader)_blockTree.FindHeader(qc.ProposedBlockInfo.Hash)
            ?? throw new IncomingMessageBlockNotFoundException(qc.ProposedBlockInfo.Hash, qc.ProposedBlockInfo.BlockNumber);

        IXdcReleaseSpec spec = _specProvider.GetXdcSpec(proposedBlockHeader, _context.CurrentRound);

        QuorumCertificate? parentQc = null;
        XdcBlockHeader? grandParent = null;

        //Can only look for a QC in proposed block after the switch block
        if (proposedBlockHeader.Number > spec.SwitchBlock)
        {
            parentQc = proposedBlockHeader.ExtraConsensusData?.QuorumCert
                ?? throw new BlockchainException("QC is targeting a block without required consensus data.");

            grandParent = FindCommitTarget(proposedBlockHeader, proposedBlockHeader.ExtraConsensusData.BlockRound);
        }

        bool committed = false;
        lock (_commitLock)
        {
            if (qc.ProposedBlockInfo.Round > _context.HighestQC.ProposedBlockInfo.Round)
                _context.HighestQC = qc;

            if (parentQc is not null && (_context.LockQC is null || parentQc.ProposedBlockInfo.Round > _context.LockQC.ProposedBlockInfo.Round))
                _context.LockQC = parentQc;

            if (grandParent is not null)
                committed = TryCommitBlock(grandParent);
        }

        if (committed)
        {
            _logger.Info($"Committed new block {grandParent!.ToString(BlockHeader.Format.Short)} round={grandParent.ExtraConsensusData!.BlockRound}");

            XdcBlockHeader parent = (XdcBlockHeader)_blockTree.FindHeader(proposedBlockHeader.ParentHash!)!;
            _ = _forensicsProcessor.ForensicsMonitoring([parent, proposedBlockHeader], qc);

            _blockTree.ForkChoiceUpdated(grandParent.Hash, grandParent.Hash);
        }

        if (qc.ProposedBlockInfo.Round >= _context.CurrentRound)
            _context.SetNewRound(qc.ProposedBlockInfo.Round + 1);
    }

    private XdcBlockHeader? FindCommitTarget(XdcBlockHeader proposedBlockHeader, ulong proposedRound)
    {
        IXdcReleaseSpec spec = _specProvider.GetXdcSpec(proposedBlockHeader);

        if ((proposedBlockHeader.Number - 2) <= spec.SwitchBlock)
        {
            if (_logger.IsDebug) _logger.Debug($"Block {proposedBlockHeader.Number} is too close to switch block {spec.SwitchBlock}, skipping commit.");
            return null;
        }

        if (_blockTree.FindHeader(proposedBlockHeader.ParentHash!) is not XdcBlockHeader parentHeader)
        {
            if (_logger.IsWarn) _logger.Warn($"Parent header {proposedBlockHeader.ParentHash} is missing.");
            return null;
        }

        if (parentHeader.ExtraConsensusData is null)
        {
            if (_logger.IsWarn) _logger.Warn($"Block {parentHeader.ToString(BlockHeader.Format.FullHashAndNumber)} does not have required consensus data! Chain might be corrupt!");
            return null;
        }

        if (proposedRound - 1 != parentHeader.ExtraConsensusData.BlockRound)
        {
            if (_logger.IsDebug) _logger.Debug($"QC round {proposedRound} is not continuous from parent QC round {parentHeader.ExtraConsensusData.BlockRound}.");
            return null;
        }

        if (_blockTree.FindHeader(parentHeader.ParentHash!) is not XdcBlockHeader grandParentHeader)
        {
            if (_logger.IsWarn) _logger.Warn($"Grandparent header {parentHeader.ParentHash} is missing.");
            return null;
        }

        if (grandParentHeader.ExtraConsensusData is null)
        {
            if (_logger.IsWarn) _logger.Warn($"QC grandparent {grandParentHeader.ToString(BlockHeader.Format.FullHashAndNumber)} does not have consensus data. Chain might be corrupt!");
            return null;
        }

        if (proposedRound - 2 != grandParentHeader.ExtraConsensusData.BlockRound)
        {
            if (_logger.IsDebug) _logger.Debug($"QC round {proposedRound} is not continuous from grandparent QC round {grandParentHeader.ExtraConsensusData.BlockRound}.");
            return null;
        }

        return grandParentHeader;
    }

    private bool TryCommitBlock(XdcBlockHeader grandParentHeader)
    {
        if (_context.HighestCommitBlock is not null && grandParentHeader.Hash == _context.HighestCommitBlock.Hash)
            return false;

        if (_context.HighestCommitBlock is not null
            && (_context.HighestCommitBlock.Round >= grandParentHeader.ExtraConsensusData!.BlockRound || _context.HighestCommitBlock.BlockNumber > grandParentHeader.Number))
        {
            if (_logger.IsDebug) _logger.Debug($"Committed block (round={_context.HighestCommitBlock.Round} #{_context.HighestCommitBlock.BlockNumber}) has higher round or block number than grandparent #{grandParentHeader.Number} round={grandParentHeader.ExtraConsensusData.BlockRound}.");
            return false;
        }

        _context.HighestCommitBlock = new BlockRoundInfo(grandParentHeader.Hash, grandParentHeader.ExtraConsensusData.BlockRound, grandParentHeader.Number);
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
        KeccakRlpWriter writer = new();
        _voteDecoder.Encode(ref writer, new Vote(proposedBlockInfo, gapNumber), RlpBehaviors.ForSealing);
        return writer.GetValueHash();
    }

    public void Initialize(XdcBlockHeader current)
    {
        IXdcReleaseSpec spec = _specProvider.GetXdcSpec(current);
        QuorumCertificate latestQc;
        if (current.Number == spec.SwitchBlock || (current.IsGenesis && current.ExtraConsensusData is null))
        {
            if (current.ExtraConsensusData is null && _logger.IsInfo)
                _logger.Info($"Block {current.ToString(BlockHeader.Format.FullHashAndNumber)} has no V2 consensus data; initializing consensus on round 1.");
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

    private void OnUpdateMainChain(object? sender, OnUpdateMainChainArgs e)
    {
        if (!e.WereProcessed)
            return;

        foreach (BlockHeader header in e.Headers)
        {
            // Violations indicate a corrupt DB; let the exception propagate.
            if (header is not XdcBlockHeader xdcHeader)
                throw new InvalidOperationException($"Expected an XDC header, but got {header.GetType().FullName}");

            if (header.IsGenesis)
            {
                Initialize(xdcHeader);
                continue;
            }

            if (xdcHeader.ExtraConsensusData is null)
                throw new InvalidOperationException($"Block {xdcHeader.ToString(BlockHeader.Format.FullHashAndNumber)} has no V2 consensus data");

            CommitCertificate(xdcHeader.ExtraConsensusData.QuorumCert);
        }
    }

    public void Dispose() => _blockTree.OnUpdateMainChain -= OnUpdateMainChain;
}
