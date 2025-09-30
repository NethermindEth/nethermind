// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Consensus;
using Nethermind.Xdc;
using Nethermind.Xdc.Types;
using Nethermind.Core.Crypto;
using Nethermind.Trie;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Nethermind.Xdc.Errors;
using Nethermind.Core.Specs;
using Nethermind.Core;
using Nethermind.Crypto;
using Nethermind.Xdc.Spec;
using Nethermind.Serialization.Rlp;
namespace Nethermind.Xdc;
internal class VotesManager : IVotesManager
{
    public VotesManager(
        XdcContext context,
        ISignatureManager xdcSignatureManager,
        IBlockTree tree,
        IEpochSwitchManager epochSwitchManager,
        ISpecProvider xdcConfig,
        PrivateKey signer,
        IBlockInfoValidator blockInfoProcessor,
        IMasternodesManager masternodesManager,
        IQuorumCertificateManager quorumCertificateManager,
        IForensicsProcessor forensicsProcessor)
    {
        Tree = tree;
        EpochSwitchManager = epochSwitchManager;
        _specProvider = xdcConfig;
        _signerKey = signer;
        BlockInfoProcessor = blockInfoProcessor;
        MasternodesManager = masternodesManager;
        QuorumCertificateManager = quorumCertificateManager;
        Context = context;
        SignatureManager = xdcSignatureManager;
        ForensicsProcessor = forensicsProcessor;
    }
    private ConcurrentBag<Vote> _votePool => new();

    public IBlockTree Tree { get; }
    public IEpochSwitchManager EpochSwitchManager { get; }
    public IBlockInfoValidator BlockInfoProcessor { get; }
    public IMasternodesManager MasternodesManager { get; }
    private IQuorumCertificateManager QuorumCertificateManager { get; }
    public XdcContext Context { get; }
    public ISignatureManager SignatureManager { get; }
    public IForensicsProcessor ForensicsProcessor { get; }

    private ISpecProvider _specProvider;

    private PrivateKey _signerKey;

    private static VoteDecoder _voteDecoder = new();
    private static EthereumEcdsa _ethereumEcdsa = new(0);


    public Task CastVote(BlockRoundInfo blockInfo)
    {
        EpochSwitchInfo epochSwitchInfo = EpochSwitchManager.GetEpochSwitchInfo(null, blockInfo.Hash);
        if (epochSwitchInfo is null)
        {
            throw new ConsensusHeaderDataExtractionException(nameof(EpochSwitchInfo));
        }
        //Optimize this by fetching with block number and round only 
        XdcBlockHeader header = Tree.FindHeader(blockInfo.Hash) as XdcBlockHeader;
        IXdcReleaseSpec spec = _specProvider.GetXdcSpec(header, blockInfo.Round);
        long epochSwitchNumber = epochSwitchInfo.EpochSwitchBlockInfo.BlockNumber;
        long gapNumber = Math.Max(0, epochSwitchNumber - epochSwitchNumber % spec.EpochLength - spec.Gap);

        var vote = new Vote(blockInfo, (ulong)gapNumber);
        Sign(vote);

        Context.HighestVotedRound = blockInfo.Round;

        HandleVote(vote);
        return Task.CompletedTask;
    }

    private void Sign(Vote vote)
    {
        KeccakRlpStream stream = new();
        _voteDecoder.Encode(stream, vote, RlpBehaviors.ForSealing);
        vote.Signature = _ethereumEcdsa.Sign(_signerKey, stream.GetValueHash());
    }

    public Task HandleVote(Vote vote)
    {
        if ((vote.ProposedBlockInfo.Round != Context.CurrentRound) && (vote.ProposedBlockInfo.Round != Context.CurrentRound + 1))
        {
            return Task.CompletedTask;
        }

        _ = ForensicsProcessor.DetectEquivocationInVotePool(vote, _votePool);
        _ = ForensicsProcessor.ProcessVoteEquivocation(vote);

        EpochSwitchInfo epochInfo = EpochSwitchManager.GetEpochSwitchInfo(null, vote.ProposedBlockInfo.Hash);
        if (epochInfo is null)
        {
            throw new ConsensusHeaderDataExtractionException(nameof(EpochSwitchInfo));
        }
        _votePool.Add(vote);

        //TODO Optimize this by fetching with block number and round only 
        XdcBlockHeader proposedHeader = Tree.FindHeader(vote.ProposedBlockInfo.Hash) as XdcBlockHeader;
        if (proposedHeader is null)
        {
            //This is a vote for a block we have not seen yet, just return for now
            return Task.CompletedTask;
        }
        IXdcReleaseSpec spec = _specProvider.GetXdcSpec(proposedHeader, vote.ProposedBlockInfo.Round);
        double certThreshold =  spec.CertThreshold;
        bool thresholdReached = _votePool.Count >= epochInfo.Masternodes.Length * certThreshold;

        if (thresholdReached)
        {
            BlockInfoProcessor.VerifyBlockInfo(vote.ProposedBlockInfo, null);

            EnsureVotesRecovered(_votePool, proposedHeader);

            OnVotePoolThresholdReached(_votePool, vote, proposedHeader);

            _votePool.Clear();
        }
        return Task.CompletedTask;
    }

    private void OnVotePoolThresholdReached(IEnumerable<Vote> tally, Vote currVote, XdcBlockHeader proposedBlockHeader)
    {
        List<Signature> validSignature = new List<Signature>();
        foreach (var vote in _votePool)
        {
            if (vote.Signature is not null)
            {
                validSignature.Add(vote.Signature);
            }
        }
        EpochSwitchInfo epochInfo = EpochSwitchManager.GetEpochSwitchInfo(null, currVote.ProposedBlockInfo.Hash);
        if (epochInfo is null)
        {
            throw new ConsensusHeaderDataExtractionException(nameof(EpochSwitchInfo));
        }
        IXdcReleaseSpec spec = _specProvider.GetXdcSpec(proposedBlockHeader, currVote.ProposedBlockInfo.Round);
        double certThreshold = spec.CertThreshold;

        if (validSignature.Count < epochInfo.Masternodes.Length * certThreshold)
        {
            return;
        }

        QuorumCertificate qc = new (currVote.ProposedBlockInfo, validSignature.ToArray(), (ulong)currVote.GapNumber);

        QuorumCertificateManager.CommitCertificate(qc);
    }

    public void EnsureVotesRecovered(IEnumerable<Vote> votes, XdcBlockHeader header)
    {
        Parallel.ForEach(votes, (v, s, a) =>
        {
            if (v.Signer is null)
            {
                v.Signer = _ethereumEcdsa.RecoverVoteSigner(v);                
            }
        });
    }

    public bool VerifyVotingRules(BlockRoundInfo blockInfo, QuorumCertificate qc)
    {
        if (Context.CurrentRound <= Context.HighestVotedRound)
        {
            return false;
        }

        if (blockInfo.Round != Context.CurrentRound)
        {
            return true;
        }

        if (Context.LockQC is null)
        {
            return true;
        }

        if (qc.ProposedBlockInfo.Round > Context.LockQC.ProposedBlockInfo.Round)
        {
            return true;
        }

        if (!isExtendingFromAncestor(blockInfo, blockInfo, Context.LockQC.ProposedBlockInfo))
        {
            return false;
        }

        return true;
    }

    private bool isExtendingFromAncestor(BlockRoundInfo blockInfo, BlockRoundInfo currentBlockInfo, BlockRoundInfo ancestorBlockInfo)
    {
        long blockNumDiff = currentBlockInfo.BlockNumber - ancestorBlockInfo.BlockNumber;

        var nextBlockHash = currentBlockInfo.Hash;

        XdcBlockHeader parentHeader = default;

        for (int i = 0; i < blockNumDiff; i++)
        {
            parentHeader = Tree.FindHeader(nextBlockHash) as XdcBlockHeader;
            if (parentHeader is null)
            {
                return false;
            }
            else
            {
                nextBlockHash = parentHeader.Hash;
            }
        }

        return nextBlockHash == ancestorBlockInfo.Hash;
    }
}
