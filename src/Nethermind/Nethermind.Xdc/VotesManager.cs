// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Consensus;
using Nethermind.Xdc;
using Nethermind.Xdc.Types;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Trie;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using BlockInfo = Nethermind.Xdc.Types.BlockInfo;
using Vote = Nethermind.Xdc.Types.Vote;
using Nethermind.Xdc.Errors;
namespace Nethermind.Xdc;
internal class VotesManager : IVotesManager
{
    public VotesManager(
        XdcContext context,
        ISignatureManager xdcSignatureManager,
        IBlockTree tree,
        IEpochSwitchManager epochSwitchManager,
        IXdcConfig xdcConfig,
        ISigner signer,
        IBlockInfoValidator blockInfoProcessor,
        IMasternodesManager masternodesManager,
        IQuorumCertificateManager quorumCertificateManager,
        IForensicsProcessor forensicsProcessor)
    {
        Tree = tree;
        EpochSwitchManager = epochSwitchManager;
        XdcConfig = xdcConfig;
        Signer = signer;
        BlockInfoProcessor = blockInfoProcessor;
        MasternodesManager = masternodesManager;
        QuorumCertificateManager = quorumCertificateManager;
        Context = context;
        SignatureManager = xdcSignatureManager;
        ForensicsProcessor = forensicsProcessor;
    }
    private List<Vote> _tally => new();

    public IBlockTree Tree { get; }
    public IEpochSwitchManager EpochSwitchManager { get; }
    public IXdcConfig XdcConfig { get; }
    public ISigner Signer { get; }
    public IBlockInfoValidator BlockInfoProcessor { get; }
    public IMasternodesManager MasternodesManager { get; }
    public IQuorumCertificateManager QuorumCertificateManager { get; }
    public XdcContext Context { get; }
    public ISignatureManager SignatureManager { get; }
    public IForensicsProcessor ForensicsProcessor { get; }
    private DateTime _votePoolCollectionTime { get; set; }

    public event Action<Vote> OnVoteCasted;

    public async Task CastVote(BlockInfo blockInfo)
    {
        if (!EpochSwitchManager.TryGetEpochSwitchInfo(null, blockInfo.Hash, out EpochSwitchInfo epochSwitchInfo))
        {
            throw new ConsensusHeaderDataExtractionException(nameof(EpochSwitchInfo));
        }

        long epochSwitchNumber = epochSwitchInfo.EpochSwitchBlockInfo.Number;
        ulong gapNumber = (ulong)Math.Max(0, epochSwitchNumber - epochSwitchNumber % (long)XdcConfig.Epoch - (long)XdcConfig.Gap);

        Signature signature = Signer.Sign(new VoteForSign(blockInfo, gapNumber).SigHash());

        Context.HighestVotedRound = Context.CurrentRound;

        var vote = new Vote(blockInfo, signature, gapNumber);
        await HandleVote(vote);

        OnVoteCasted?.Invoke(vote);
    }

    public async Task HandleVote(Vote vote)
    {
        if ((vote.ProposedBlockInfo.Round != Context.CurrentRound) && (vote.ProposedBlockInfo.Round != Context.CurrentRound + 1))
        {
            throw new Exception("ErrIncomingMessageRoundTooFarFromCurrentRound");
        }

        if (_votePoolCollectionTime == default)
        {
            _votePoolCollectionTime = DateTime.UtcNow;
        }

        _tally.Add(vote);
        _ = ForensicsProcessor.DetectEquivocationInVotePool(vote, _tally);
        _ = ForensicsProcessor.ProcessVoteEquivocation(vote);

        if (EpochSwitchManager.TryGetEpochSwitchInfo(null, vote.ProposedBlockInfo.Hash, out EpochSwitchInfo epochInfo))
        {
            throw new ConsensusHeaderDataExtractionException(nameof(EpochSwitchInfo));
        }

        double certThreshold = XdcConfig.Configs[vote.ProposedBlockInfo.Round].CertThreshold;
        bool thresholdReached = _tally.Count >= epochInfo.Masternodes.Length * certThreshold;

        if (thresholdReached)
        {
            var proposedBlockHeader = (XdcBlockHeader)Tree.FindHeader(vote.ProposedBlockInfo.Hash);
            if (proposedBlockHeader is null)
            {
                return;
            }

            BlockInfoProcessor.VerifyBlockInfo(vote.ProposedBlockInfo, null);

            await VerifyVotes(_tally, proposedBlockHeader);

            onVotePoolThresholdReached(_tally, vote, proposedBlockHeader);

            _votePoolCollectionTime = default;
        }
    }

    private void onVotePoolThresholdReached(List<Vote> tally, Vote currVote, XdcBlockHeader proposedBlockHeader)
    {
        List<Signature> validSignature = new List<Signature>();
        foreach (var vote in _tally)
        {
            if (vote.GetSigner() != default)
            {
                validSignature.Add(vote.Signature);
            }
        }

        if (!EpochSwitchManager.TryGetEpochSwitchInfo(null, currVote.ProposedBlockInfo.Hash, out EpochSwitchInfo epochInfo))
        {
            throw new ConsensusHeaderDataExtractionException(nameof(EpochSwitchInfo));
        }

        double certThreshold = XdcConfig.Configs[currVote.ProposedBlockInfo.Round].CertThreshold;

        if (validSignature.Count < epochInfo.Masternodes.Length * certThreshold)
        {
            return;
        }

        QuorumCert qc = new QuorumCert(currVote.ProposedBlockInfo, validSignature.ToArray(), currVote.GapNumber);

        QuorumCertificateManager.CommitCertificate(qc);
    }

    public async Task VerifyVotes(List<Vote> votes, XdcBlockHeader header)
    {
        Address[] masternodes = MasternodesManager.GetMasternodes(header);

        List<Task> tasks = new List<Task>();

        foreach (var vote in votes)
        {
            tasks.Add(Task.Factory.StartNew(() =>
            {
                Address signerAddress = vote.GetSigner();
                if (signerAddress != default)
                {
                    if (masternodes.Length == 0)
                    {
                        throw new Exception("empty masternode list detected when verifying message signatures");
                    }

                    foreach (var masternode in masternodes)
                    {
                        if (masternode == signerAddress)
                        {
                            return;
                        }

                        vote.SetSigner(default);
                        return;
                    }
                }

                Hash256 signedVote = new VoteForSign(vote.ProposedBlockInfo, vote.GapNumber).SigHash();
                if (!SignatureManager.VerifyMessageSignature(signedVote, vote.Signature, masternodes, out Address masterNode))
                {
                    return;
                }

                vote.SetSigner(masterNode);
            }));
        }

        await Task.WhenAll(tasks);
    }

    public bool VerifyVotingRules(BlockInfo blockInfo, QuorumCert qc)
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

    private bool isExtendingFromAncestor(BlockInfo blockInfo, BlockInfo currentBlockInfo, BlockInfo ancestorBlockInfo)
    {
        long blockNumDiff = currentBlockInfo.Number - ancestorBlockInfo.Number;

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

    public List<Vote> GetVotes()
    {
        return _tally;
    }
}
