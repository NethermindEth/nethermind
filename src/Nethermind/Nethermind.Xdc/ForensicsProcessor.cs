// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Serialization.Rlp;
using Nethermind.Xdc.Types;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Nethermind.Xdc.RLP;

namespace Nethermind.Xdc;

internal class ForensicsProcessor(IBlockTree blockTree, IEpochSwitchManager epochSwitchManager, ILogManager logManager) : IForensicsProcessor
{
    private const int NumberOfForensicsQcs = 3;
    private const long MaxForensicsTraversalDepth = 256;

    private readonly IBlockTree _blockTree = blockTree;
    private readonly IEpochSwitchManager _epochSwitchManager = epochSwitchManager;
    private readonly ILogger _logger = logManager.GetClassLogger<ForensicsProcessor>();
    private static readonly EthereumEcdsa _ethereumEcdsa = new(0);
    private static readonly VoteDecoder _voteDecoder = new();
    private readonly object _highestCommittedQcsLock = new();
    private QuorumCertificate[] _highestCommittedQcs = [];
    public event EventHandler<ForensicsEvent>? ForensicsEventEmitted;

    public Task ForensicsMonitoring(IEnumerable<XdcBlockHeader> headerQcToBeCommitted, QuorumCertificate incomingQC)
    {
        ProcessForensics(incomingQC);
        return SetCommittedQCs(headerQcToBeCommitted, incomingQC);
    }

    internal Task ProcessForensics(QuorumCertificate incomingQC)
    {
        QuorumCertificate[] highestCommittedQCs;
        lock (_highestCommittedQcsLock)
        {
            highestCommittedQCs = _highestCommittedQcs;
        }

        if (highestCommittedQCs.Length != NumberOfForensicsQcs)
        {
            if (_logger.IsDebug) _logger.Debug("[ProcessForensics] HighestCommittedQCs value not set");
            return Task.CompletedTask;
        }

        if (!TryFindAncestorQCs(incomingQC, 2, out QuorumCertificate[] incomingQuorumCerts))
        {
            return Task.CompletedTask;
        }

        if (CheckQCsOnTheSameChain(highestCommittedQCs, incomingQuorumCerts))
        {
            if (_logger.IsDebug) _logger.Debug("[ProcessForensics] Passed checking, nothing suspicious to report");
            return Task.CompletedTask;
        }

        if (TryFindQCsInSameRound(highestCommittedQCs, incomingQuorumCerts, out QuorumCertificate? sameRoundHCQC, out QuorumCertificate? sameRoundQC))
        {
            return SendForensicProof(sameRoundHCQC, sameRoundQC);
        }

        if (!TryFindAncestorQcThroughRound(highestCommittedQCs, incomingQuorumCerts, out QuorumCertificate? ancestorQC, out QuorumCertificate[] lowerRoundQCs))
        {
            if (_logger.IsDebug) _logger.Debug("[ProcessForensics] Failed to find ancestor QC through round");
            return Task.CompletedTask;
        }

        return SendForensicProof(ancestorQC, lowerRoundQCs[NumberOfForensicsQcs - 1]);
    }

    internal Task SetCommittedQCs(IEnumerable<XdcBlockHeader> headers, QuorumCertificate incomingQC)
    {
        XdcBlockHeader[] committedHeaders = headers.ToArray();
        if (committedHeaders.Length != NumberOfForensicsQcs - 1)
        {
            if (_logger.IsError) _logger.Error("[SetCommittedQCs] Received input length not equal to 2");
            return Task.CompletedTask;
        }

        QuorumCertificate[] committedQCs = new QuorumCertificate[NumberOfForensicsQcs];
        for (int i = 0; i < committedHeaders.Length; i++)
        {
            XdcBlockHeader header = committedHeaders[i];
            QuorumCertificate? qc = header.ExtraConsensusData?.QuorumCert;
            if (qc is null)
            {
                if (_logger.IsError) _logger.Error($"[SetCommittedQCs] Failed to decode extra for index {i}");
                return Task.CompletedTask;
            }

            if (i != 0)
            {
                if (qc.ProposedBlockInfo.Hash != committedHeaders[i - 1].Hash)
                {
                    if (_logger.IsError) _logger.Error("[SetCommittedQCs] Headers are not on same chain and order");
                    return Task.CompletedTask;
                }
                if (i == committedHeaders.Length - 1 && incomingQC.ProposedBlockInfo.Hash != header.Hash)
                {
                    if (_logger.IsError) _logger.Error("[SetCommittedQCs] incomingQC does not point at last header");
                    return Task.CompletedTask;
                }
            }

            committedQCs[i] = qc;
        }

        committedQCs[NumberOfForensicsQcs - 1] = incomingQC;
        lock (_highestCommittedQcsLock)
        {
            _highestCommittedQcs = committedQCs;
        }
        return Task.CompletedTask;
    }

    public Task DetectEquivocationInVotePool(Vote vote, IEnumerable<Vote> votePool)
    {
        if (!TryGetVoteSigner(vote, out Address? signer))
        {
            return Task.CompletedTask;
        }

        foreach (Vote pooledVote in votePool)
        {
            if (!TryGetVoteSigner(pooledVote, out Address? pooledSigner))
            {
                continue;
            }

            if (pooledSigner == signer)
            {
                return SendVoteEquivocationProof(vote, pooledVote, signer);
            }
        }

        return Task.CompletedTask;
    }

    // Internal test hook to inspect committed QC state.
    internal QuorumCertificate[] GetHighestCommittedQcsSnapshot()
    {
        lock (_highestCommittedQcsLock)
        {
            return _highestCommittedQcs.ToArray();
        }
    }

    internal (Hash256 AncestorHash, IList<string> FirstPath, IList<string> SecondPath) FindAncestorBlockHash(BlockRoundInfo firstBlockInfo, BlockRoundInfo secondBlockInfo)
    {
        Hash256 lowerBlockNumHash = firstBlockInfo.Hash;
        Hash256 higherBlockNumberHash = secondBlockInfo.Hash;

        List<string> lowerBlockNumToAncestorHashPath = [];
        List<string> higherBlockToAncestorNumHashPath = [];
        bool orderSwapped = secondBlockInfo.BlockNumber < firstBlockInfo.BlockNumber;

        ulong blockNumberDifference;
        if (orderSwapped)
        {
            lowerBlockNumHash = secondBlockInfo.Hash;
            higherBlockNumberHash = firstBlockInfo.Hash;
            blockNumberDifference = firstBlockInfo.BlockNumber - secondBlockInfo.BlockNumber;
        }
        else
        {
            blockNumberDifference = secondBlockInfo.BlockNumber - firstBlockInfo.BlockNumber;
        }

        if (blockNumberDifference > MaxForensicsTraversalDepth)
        {
            if (_logger.IsWarn)
            {
                _logger.Warn($"[FindAncestorBlockHash] Traversal depth {blockNumberDifference} exceeded cap {MaxForensicsTraversalDepth}.");
            }
            return (Hash256.Zero, lowerBlockNumToAncestorHashPath, higherBlockToAncestorNumHashPath);
        }

        lowerBlockNumToAncestorHashPath.Add(lowerBlockNumHash.ToString());
        higherBlockToAncestorNumHashPath.Add(higherBlockNumberHash.ToString());

        for (int i = 0; i < (int)blockNumberDifference; i++)
        {
            XdcBlockHeader? parentHeader = (XdcBlockHeader?)_blockTree.FindHeader(higherBlockNumberHash);
            if (parentHeader is null || parentHeader.ParentHash is null)
            {
                return (Hash256.Zero, lowerBlockNumToAncestorHashPath, higherBlockToAncestorNumHashPath);
            }

            higherBlockNumberHash = parentHeader.ParentHash!;
            higherBlockToAncestorNumHashPath.Add(higherBlockNumberHash.ToString());
        }

        long convergenceSteps = 0;
        while (lowerBlockNumHash != higherBlockNumberHash)
        {
            if (convergenceSteps++ >= MaxForensicsTraversalDepth)
            {
                if (_logger.IsWarn)
                {
                    _logger.Warn($"[FindAncestorBlockHash] Convergence traversal exceeded cap {MaxForensicsTraversalDepth}.");
                }
                return (Hash256.Zero, lowerBlockNumToAncestorHashPath, higherBlockToAncestorNumHashPath);
            }

            XdcBlockHeader? lowerHeader = (XdcBlockHeader?)_blockTree.FindHeader(lowerBlockNumHash);
            XdcBlockHeader? higherHeader = (XdcBlockHeader?)_blockTree.FindHeader(higherBlockNumberHash);
            if (lowerHeader is null || higherHeader is null || lowerHeader.ParentHash is null || higherHeader.ParentHash is null)
            {
                return (Hash256.Zero, lowerBlockNumToAncestorHashPath, higherBlockToAncestorNumHashPath);
            }

            lowerBlockNumHash = lowerHeader.ParentHash!;
            higherBlockNumberHash = higherHeader.ParentHash!;
            lowerBlockNumToAncestorHashPath.Add(lowerBlockNumHash.ToString());
            higherBlockToAncestorNumHashPath.Add(higherBlockNumberHash.ToString());
        }

        lowerBlockNumToAncestorHashPath.Reverse();
        higherBlockToAncestorNumHashPath.Reverse();

        if (orderSwapped)
        {
            return (lowerBlockNumHash, higherBlockToAncestorNumHashPath, lowerBlockNumToAncestorHashPath);
        }

        return (lowerBlockNumHash, lowerBlockNumToAncestorHashPath, higherBlockToAncestorNumHashPath);
    }

    private bool CheckQCsOnTheSameChain(QuorumCertificate[] highestCommittedQCs, QuorumCertificate[] incomingQCAndParents)
    {
        QuorumCertificate[] lowerBlockNumQCs = highestCommittedQCs;
        QuorumCertificate[] higherBlockNumQCs = incomingQCAndParents;
        if (incomingQCAndParents[0].ProposedBlockInfo.BlockNumber < highestCommittedQCs[0].ProposedBlockInfo.BlockNumber)
        {
            lowerBlockNumQCs = incomingQCAndParents;
            higherBlockNumQCs = highestCommittedQCs;
        }

        BlockRoundInfo proposedBlockInfo = higherBlockNumQCs[0].ProposedBlockInfo;
        ulong blockDifference = higherBlockNumQCs[0].ProposedBlockInfo.BlockNumber - lowerBlockNumQCs[0].ProposedBlockInfo.BlockNumber;
        if (blockDifference > MaxForensicsTraversalDepth)
        {
            return false;
        }

        for (int i = 0; i < (int)blockDifference; i++)
        {
            XdcBlockHeader? parentHeader = (XdcBlockHeader?)_blockTree.FindHeader(proposedBlockInfo.Hash);
            QuorumCertificate? parentQc = parentHeader?.ExtraConsensusData?.QuorumCert;
            if (parentQc is null)
                return false;

            proposedBlockInfo = parentQc.ProposedBlockInfo;
        }

        bool sameAnchor = proposedBlockInfo.Hash == lowerBlockNumQCs[0].ProposedBlockInfo.Hash
            && proposedBlockInfo.BlockNumber == lowerBlockNumQCs[0].ProposedBlockInfo.BlockNumber
            && proposedBlockInfo.Round == lowerBlockNumQCs[0].ProposedBlockInfo.Round;
        if (!sameAnchor)
        {
            return false;
        }

        // Also compare the newest QC tips to avoid classifying diverged forks as same-chain
        // when they still share the oldest ancestor in the 3-QC window.
        BlockRoundInfo highestTip = highestCommittedQCs[NumberOfForensicsQcs - 1].ProposedBlockInfo;
        BlockRoundInfo incomingTip = incomingQCAndParents[NumberOfForensicsQcs - 1].ProposedBlockInfo;
        if (highestTip.BlockNumber == incomingTip.BlockNumber)
        {
            return highestTip.Hash == incomingTip.Hash && highestTip.Round == incomingTip.Round;
        }

        return highestTip.BlockNumber < incomingTip.BlockNumber
            ? IsExtendingFromAncestor(incomingTip, highestTip)
            : IsExtendingFromAncestor(highestTip, incomingTip);
    }

    internal bool TryFindQCsInSameRound(
        QuorumCertificate[] quorumCerts1,
        QuorumCertificate[] quorumCerts2,
        [NotNullWhen(true)] out QuorumCertificate? first,
        [NotNullWhen(true)] out QuorumCertificate? second)
    {
        for (int i = 0; i < quorumCerts1.Length; i++)
        {
            QuorumCertificate quorumCert1 = quorumCerts1[i];
            for (int j = 0; j < quorumCerts2.Length; j++)
            {
                QuorumCertificate quorumCert2 = quorumCerts2[j];
                if (quorumCert1.ProposedBlockInfo.Round == quorumCert2.ProposedBlockInfo.Round)
                {
                    first = quorumCert1;
                    second = quorumCert2;
                    return true;
                }
            }
        }

        first = null;
        second = null;
        return false;
    }

    private bool TryFindAncestorQCs(QuorumCertificate currentQc, int distanceFromCurrentQc, out QuorumCertificate[] quorumCertsAscending)
    {
        List<QuorumCertificate> quorumCerts = [currentQc];
        QuorumCertificate quorumCertificate = currentQc;
        for (int i = 0; i < distanceFromCurrentQc; i++)
        {
            XdcBlockHeader? parentHeader = (XdcBlockHeader?)_blockTree.FindHeader(quorumCertificate.ProposedBlockInfo.Hash);
            QuorumCertificate? parentQc = parentHeader?.ExtraConsensusData?.QuorumCert;
            if (parentQc is null)
            {
                quorumCertsAscending = [];
                return false;
            }

            quorumCertificate = parentQc;
            quorumCerts.Add(quorumCertificate);
        }

        quorumCerts.Reverse();
        quorumCertsAscending = [.. quorumCerts];
        return true;
    }

    private bool TryFindAncestorQcThroughRound(
        QuorumCertificate[] highestCommittedQCs,
        QuorumCertificate[] incomingQCAndItsParents,
        [NotNullWhen(true)] out QuorumCertificate? ancestorQc,
        out QuorumCertificate[] lowerRoundQCs)
    {
        lowerRoundQCs = highestCommittedQCs;
        QuorumCertificate[] higherRoundQCs = incomingQCAndItsParents;
        if (incomingQCAndItsParents[0].ProposedBlockInfo.Round < highestCommittedQCs[0].ProposedBlockInfo.Round)
        {
            lowerRoundQCs = incomingQCAndItsParents;
            higherRoundQCs = highestCommittedQCs;
        }

        ancestorQc = higherRoundQCs[0];
        ulong targetRound = lowerRoundQCs[NumberOfForensicsQcs - 1].ProposedBlockInfo.Round;
        while (ancestorQc.ProposedBlockInfo.Round >= targetRound)
        {
            Hash256 ancestorHash = ancestorQc.ProposedBlockInfo.Hash;
            XdcBlockHeader? proposedBlock = (XdcBlockHeader?)_blockTree.FindHeader(ancestorHash);
            QuorumCertificate? parentQc = proposedBlock?.ExtraConsensusData?.QuorumCert;
            if (parentQc is null)
            {
                return false;
            }

            if (parentQc.ProposedBlockInfo.Round < targetRound)
            {
                return true;
            }

            if (parentQc.ProposedBlockInfo.Round >= ancestorQc.ProposedBlockInfo.Round)
            {
                // Defensive: without strictly decreasing rounds, malformed ancestry may loop forever.
                if (_logger.IsWarn)
                {
                    _logger.Warn(
                        $"[TryFindAncestorQcThroughRound] Non-decreasing QC round transition " +
                        $"({ancestorQc.ProposedBlockInfo.Round} -> {parentQc.ProposedBlockInfo.Round}) " +
                        $"at block {ancestorHash}.");
                }
                return false;
            }

            ancestorQc = parentQc;
        }

        return false;
    }

    private IReadOnlyList<string> GetQcSignerAddresses(QuorumCertificate quorumCert)
    {
        Signature[] signatures = quorumCert.Signatures ?? [];
        List<string> signerList = new(signatures.Length);
        Vote signVote = new(quorumCert.ProposedBlockInfo, quorumCert.GapNumber);
        ValueHash256 signHash = VoteHash(signVote);
        for (int i = 0; i < signatures.Length; i++)
        {
            Address? signer = _ethereumEcdsa.RecoverAddress(signatures[i], in signHash);
            if (signer is not null)
                signerList.Add(signer.ToString());
        }

        return signerList;
    }

    private static string GenerateForensicsId(Hash256 divergingHash, QuorumCertificate qc1, QuorumCertificate qc2)
        => $"{divergingHash}:{qc1.ProposedBlockInfo.Hash}:{qc2.ProposedBlockInfo.Hash}";

    private static string GenerateVoteEquivocationId(Address signer, ulong round1, ulong round2)
        => $"{signer}:{round1}:{round2}";

    private bool TryGetVoteSigner(Vote vote, [NotNullWhen(true)] out Address? signer)
    {
        if (vote.Signer is not null)
        {
            signer = vote.Signer;
            return true;
        }

        if (vote.Signature is null)
        {
            signer = null;
            return false;
        }

        ValueHash256 signHash = VoteHash(new Vote(vote.ProposedBlockInfo, vote.GapNumber));
        signer = _ethereumEcdsa.RecoverAddress(vote.Signature, in signHash);
        if (signer is null)
            return false;

        vote.Signer = signer;
        return true;
    }

    private ValueHash256 VoteHash(Vote vote)
    {
        KeccakRlpWriter writer = new();
        _voteDecoder.Encode(ref writer, vote, RlpBehaviors.ForSealing);
        return writer.GetValueHash();
    }

    private bool IsExtendingFromAncestor(BlockRoundInfo currentBlock, BlockRoundInfo ancestorBlock)
    {
        // Callers do not all guarantee ordering, so guard against ulong underflow explicitly.
        if (currentBlock.BlockNumber < ancestorBlock.BlockNumber)
        {
            return false;
        }
        ulong blockNumDiff = currentBlock.BlockNumber - ancestorBlock.BlockNumber;
        if (blockNumDiff > MaxForensicsTraversalDepth)
        {
            return false;
        }

        Hash256 nextBlockHash = currentBlock.Hash;
        for (int i = 0; i < (int)blockNumDiff; i++)
        {
            XdcBlockHeader? parentBlock = (XdcBlockHeader?)_blockTree.FindHeader(nextBlockHash);
            if (parentBlock is null || parentBlock.ParentHash is null)
            {
                return false;
            }

            nextBlockHash = parentBlock.ParentHash!;
        }

        return nextBlockHash == ancestorBlock.Hash;
    }

    private bool IsVoteBlamed(QuorumCertificate[] highestCommittedQCs, Vote incomingVote, out QuorumCertificate? parentQc)
    {
        XdcBlockHeader? proposedBlock = (XdcBlockHeader?)_blockTree.FindHeader(incomingVote.ProposedBlockInfo.Hash);
        parentQc = proposedBlock?.ExtraConsensusData?.QuorumCert;
        if (parentQc is null)
        {
            return false;
        }

        return parentQc.ProposedBlockInfo.Round < highestCommittedQCs[NumberOfForensicsQcs - 1].ProposedBlockInfo.Round;
    }

    public Task ProcessVoteEquivocation(Vote incomingVote)
    {
        QuorumCertificate[] highestCommittedQCs;
        lock (_highestCommittedQcsLock)
        {
            highestCommittedQCs = _highestCommittedQcs;
        }

        if (highestCommittedQCs.Length != NumberOfForensicsQcs)
        {
            // Forensics baseline has not been initialized yet.
            return Task.CompletedTask;
        }

        if (incomingVote.ProposedBlockInfo.Round < highestCommittedQCs[NumberOfForensicsQcs - 1].ProposedBlockInfo.Round)
        {
            // Ignore stale votes that predate the latest committed QC snapshot.
            return Task.CompletedTask;
        }

        if (IsExtendingFromAncestor(incomingVote.ProposedBlockInfo, highestCommittedQCs[0].ProposedBlockInfo))
        {
            // Vote extends the committed chain, so this path is not suspicious.
            return Task.CompletedTask;
        }

        if (!IsVoteBlamed(highestCommittedQCs, incomingVote, out QuorumCertificate? parentQc))
        {
            if (parentQc is not null)
            {
                return ProcessForensics(parentQc);
            }

            return Task.CompletedTask;
        }

        if (TryGetVoteSigner(incomingVote, out Address? signer))
        {
            QuorumCertificate qc = highestCommittedQCs[NumberOfForensicsQcs - 1];
            Signature[] signatures = qc.Signatures ?? [];
            for (int i = 0; i < signatures.Length; i++)
            {
                Vote voteFromQc = new(qc.ProposedBlockInfo, qc.GapNumber, signatures[i]);
                if (!TryGetVoteSigner(voteFromQc, out Address? signerFromQc))
                {
                    continue;
                }

                if (signerFromQc == signer)
                {
                    return SendVoteEquivocationProof(incomingVote, voteFromQc, signer);
                }
            }
        }

        return Task.CompletedTask;
    }

    internal Task SendForensicProof(QuorumCertificate firstQc, QuorumCertificate secondQc)
    {
        QuorumCertificate lowerRoundQc = firstQc;
        QuorumCertificate higherRoundQc = secondQc;
        if (secondQc.ProposedBlockInfo.Round < firstQc.ProposedBlockInfo.Round)
        {
            lowerRoundQc = secondQc;
            higherRoundQc = firstQc;
        }

        (Hash256 ancestorHash, IList<string> ancestorToLowerRoundPath, IList<string> ancestorToHigherRoundPath) = FindAncestorBlockHash(
            lowerRoundQc.ProposedBlockInfo,
            higherRoundQc.ProposedBlockInfo);
        XdcBlockHeader? ancestorHeader = (XdcBlockHeader?)_blockTree.FindHeader(ancestorHash);
        if (ancestorHash == Hash256.Zero || ancestorHeader is null)
        {
            return Task.CompletedTask;
        }

        EpochSwitchInfo? lowerRoundQcEpochSwitchInfo = _epochSwitchManager.GetEpochSwitchInfo(lowerRoundQc.ProposedBlockInfo.Hash);
        EpochSwitchInfo? higherRoundQcEpochSwitchInfo = _epochSwitchManager.GetEpochSwitchInfo(higherRoundQc.ProposedBlockInfo.Hash);
        bool acrossEpochs = lowerRoundQcEpochSwitchInfo is not null
            && higherRoundQcEpochSwitchInfo is not null
            && lowerRoundQcEpochSwitchInfo.EpochSwitchBlockInfo.Hash != higherRoundQcEpochSwitchInfo.EpochSwitchBlockInfo.Hash;

        ForensicsContent content = new()
        {
            DivergingBlockHash = ancestorHash.ToString(),
            AcrossEpoch = acrossEpochs,
            DivergingBlockNumber = ancestorHeader.Number,
            SmallerRoundInfo = new ForensicsInfo
            {
                HashPath = ancestorToLowerRoundPath,
                QuorumCert = lowerRoundQc,
                SignerAddresses = GetQcSignerAddresses(lowerRoundQc)
            },
            LargerRoundInfo = new ForensicsInfo
            {
                HashPath = ancestorToHigherRoundPath,
                QuorumCert = higherRoundQc,
                SignerAddresses = GetQcSignerAddresses(higherRoundQc)
            }
        };

        ForensicProof forensicsProof = new()
        {
            Id = GenerateForensicsId(ancestorHash, lowerRoundQc, higherRoundQc),
            ForensicsType = "QC",
            Content = JsonSerializer.Serialize(content, EthereumJsonSerializer.JsonOptions) ?? string.Empty
        };

        if (_logger.IsInfo) _logger.Info($"Forensics proof generated: {forensicsProof.Content}");
        ForensicsEventEmitted?.Invoke(this, new ForensicsEvent { ForensicsProof = forensicsProof });
        return Task.CompletedTask;
    }

    internal Task SendVoteEquivocationProof(Vote vote1, Vote vote2, Address signer)
    {
        Vote smallerRoundVote = vote1;
        Vote largerRoundVote = vote2;
        if (vote1.ProposedBlockInfo.Round > vote2.ProposedBlockInfo.Round)
        {
            smallerRoundVote = vote2;
            largerRoundVote = vote1;
        }

        VoteEquivocationContent content = new()
        {
            SmallerRoundVote = smallerRoundVote,
            LargerRoundVote = largerRoundVote,
            Signer = signer
        };

        ForensicProof forensicsProof = new()
        {
            Id = GenerateVoteEquivocationId(signer, smallerRoundVote.ProposedBlockInfo.Round, largerRoundVote.ProposedBlockInfo.Round),
            ForensicsType = "Vote",
            Content = JsonSerializer.Serialize(content, EthereumJsonSerializer.JsonOptions) ?? string.Empty
        };

        if (_logger.IsInfo) _logger.Info($"Forensics vote-equivocation proof generated: {forensicsProof.Content}");
        ForensicsEventEmitted?.Invoke(this, new ForensicsEvent { ForensicsProof = forensicsProof });
        return Task.CompletedTask;
    }
}
