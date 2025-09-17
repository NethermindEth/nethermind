// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;
using Nethermind.Blockchain;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Logging;
using Nethermind.Xdc;
using Nethermind.Xdc.Errors;
using Nethermind.Xdc.Types;
using BlockInfo = Nethermind.Xdc.Types.BlockInfo;

namespace Nethermind.Xdc;
internal class QuorumCertificateManager : IQuorumCertificateManager
{
    public QuorumCertificateManager(
        XdcContext context,
        IBlockTree chain,
        IXdcConfig xdcConfig,
        ISignatureManager xdcSignatureManager,
        IEpochSwitchManager epochSwitchManager,
        IBlockInfoValidator blockInfoProcessor,
        IForensicsProcessor forensicsProcessor)
    {
        _context = context;
        _chain = chain;
        _config = xdcConfig;
        _signatureManager = xdcSignatureManager;
        _epochSwitchManager = epochSwitchManager;
        _blockInfoProcessor = blockInfoProcessor;
        _forensicsProcessor = forensicsProcessor;
    }

    private XdcContext _context { get; }
    private IBlockTree _chain { get; }
    private IXdcConfig _config { get; }
    private ISignatureManager _signatureManager { get; }
    private IEpochSwitchManager _epochSwitchManager { get; }
    private IBlockInfoValidator _blockInfoProcessor { get; }
    private IForensicsProcessor _forensicsProcessor { get; }

    public void CommitCertificate(QuorumCert qc)
    {
        if (qc.ProposedBlockInfo.Round > _context.HighestQC.ProposedBlockInfo.Round)
        {
            _context.HighestQC = qc;
        }

        var proposedBlockHeader = (XdcBlockHeader)_chain.FindHeader(qc.ProposedBlockInfo.Hash);
        if (proposedBlockHeader is null)
        {
            throw new InvalidBlockException(proposedBlockHeader, "Proposed block header not found in chain");
        }

        if (proposedBlockHeader.Number > 0)
        {
            if (!Utils.TryGetExtraFields(proposedBlockHeader, _config.SwitchBlock, out QuorumCert proposedQc, out ulong round, out _))
            {
                throw new ConsensusHeaderDataExtractionException(nameof(ExtraFieldsV2));
            }

            if (_context.LockQC is null || proposedQc.ProposedBlockInfo.Round > _context.LockQC.ProposedBlockInfo.Round)
            {
                _context.LockQC = proposedQc;
            }

            CommitBlock(_chain, proposedBlockHeader, round, qc);
        }

        if (qc.ProposedBlockInfo.Round >= _context.CurrentRound)
        {
            _context.SetNewRound(_chain, qc.ProposedBlockInfo.Round);
        }
    }

    private bool CommitBlock(IBlockTree chain, XdcBlockHeader proposedBlockHeader, ulong proposedRound, QuorumCert proposedQuorumCert)
    {
        if ((proposedBlockHeader.Number - 2) <= _config.SwitchBlock)
        {
            return false;
        }

        XdcBlockHeader parentHeader = (XdcBlockHeader)_chain.FindHeader(proposedBlockHeader.ParentHash);

        if (!Utils.TryGetExtraFields(parentHeader, _config.SwitchBlock, out _, out ulong round, out _))
        {
            return false;
        }

        if (proposedRound - 1 != round)
        {
            return false;
        }

        XdcBlockHeader grandParentHeader = (XdcBlockHeader)_chain.FindHeader(parentHeader.ParentHash);

        if (!Utils.TryGetExtraFields(grandParentHeader, _config.SwitchBlock, out _, out round, out _))
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
        _ = _forensicsProcessor.ForensicsMonitoring(headerQcToBeCommitted, proposedQuorumCert);

        return true;
    }

    public void VerifyCertificate(QuorumCert qc, XdcBlockHeader parentHeader)
    {
        if (qc is null)
            throw new ArgumentNullException(nameof(qc));

        if (!_epochSwitchManager.TryGetEpochSwitchInfo(parentHeader, qc.ProposedBlockInfo.Hash, out EpochSwitchInfo epochSwitchInfo))
        {
            throw new ConsensusHeaderDataExtractionException(nameof(EpochSwitchInfo));
        }

        (var signatures, var duplicates) = Utils.FilterSignatures(qc.Signatures);

        ulong qcRound = qc.ProposedBlockInfo.Round;
        double certThreshold = _config.Configs[qcRound].CertThreshold;

        if ((qcRound > 0) && (signatures is null || signatures.Count < epochSwitchInfo.Masternodes.Length * certThreshold))
        {
            throw new CertificateValidationException(CertificateType.QuorumCertificate, CertificateValidationFailure.InvalidSignatures);
        }

        try
        {
            // Launch one Task per signature
            var tasks = new List<Task>();

            var voteForSignObj = new VoteForSign(qc.ProposedBlockInfo, qc.GapNumber).SigHash();

            foreach (var signature in signatures)
            {
                tasks.Add(Task.Run(() =>
                {
                    if (!_signatureManager.VerifyMessageSignature(voteForSignObj, signature, epochSwitchInfo.Masternodes, out Address _))
                    {
                        throw new CertificateValidationException(CertificateType.QuorumCertificate, CertificateValidationFailure.InvalidSignatures);
                    }
                }));

                Task.WaitAll(tasks);
            }

            long epochSwitchNumber = epochSwitchInfo.EpochSwitchParentBlockInfo.Number;
            long gapNumber = epochSwitchNumber - (epochSwitchNumber % (long)_config.Epoch) - (long)_config.Gap;

            if (gapNumber < 0) gapNumber = 0;

            if (gapNumber != qc.GapNumber)
            {
                throw new CertificateValidationException(CertificateType.QuorumCertificate, CertificateValidationFailure.InvalidGapNumber);
            }

            // Note : this method should be moved outside of Context
            _blockInfoProcessor.VerifyBlockInfo(qc.ProposedBlockInfo, parentHeader);
        }
        catch
        {
            throw;
        }
    }
}
