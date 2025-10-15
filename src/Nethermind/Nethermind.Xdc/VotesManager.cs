// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Consensus;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Threading;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Xdc.Spec;
using Nethermind.Xdc.Types;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Core;

namespace Nethermind.Xdc;
internal class VotesManager (
    XdcContext context,
    IBlockTree tree,
    IEpochSwitchManager epochSwitchManager,
    ISnapshotManager snapshotManager,
    ISpecProvider specProvider,
    ISigner signer,
    IForensicsProcessor forensicsProcessor) : IVotesManager
{
    private IBlockTree _tree = tree;
    private IEpochSwitchManager _epochSwitchManager = epochSwitchManager;
    private ISnapshotManager _snapshotManager = snapshotManager;
    private XdcContext _ctx = context;
    private IForensicsProcessor _forensicsProcessor = forensicsProcessor;
    private ISpecProvider _specProvider = specProvider;
    private ISigner _signer = signer;

    private XdcPool<Vote> _votePool = new();
    private static VoteDecoder _voteDecoder = new();
    private static EthereumEcdsa _ethereumEcdsa = new(0);

    public Task CastVote(BlockRoundInfo blockInfo)
    {
        EpochSwitchInfo epochSwitchInfo = _epochSwitchManager.GetEpochSwitchInfo(null, blockInfo.Hash);
        if (epochSwitchInfo is null)
            throw new ArgumentException($"Cannot find epoch info for block {blockInfo.Hash}",nameof(EpochSwitchInfo));
        //Optimize this by fetching with block number and round only

        XdcBlockHeader header = _tree.FindHeader(blockInfo.Hash) as XdcBlockHeader;
        IXdcReleaseSpec spec = _specProvider.GetXdcSpec(header, blockInfo.Round);
        long epochSwitchNumber = epochSwitchInfo.EpochSwitchBlockInfo.BlockNumber;
        long gapNumber = Math.Max(0, epochSwitchNumber - epochSwitchNumber % spec.EpochLength - spec.Gap);

        var vote = new Vote(blockInfo, (ulong)gapNumber);
        // Sets signature and signer for the vote
        Sign(vote);

        _ctx.HighestVotedRound = blockInfo.Round;

        HandleVote(vote);
        //TODO Broadcast vote to peers
        return Task.CompletedTask;
    }

    public Task HandleVote(Vote vote)
    {
        if ((vote.ProposedBlockInfo.Round != _ctx.CurrentRound) && (vote.ProposedBlockInfo.Round != _ctx.CurrentRound + 1))
        {
            //We only care about votes for the current round or the next round
            return Task.CompletedTask;
        }

        // Collect votes
        //TODO check for duplicate votes from the same signer when adding
        _votePool.Add(vote);
        IReadOnlyCollection<Vote> roundVotes = _votePool.GetItems(vote.ProposedBlockInfo.Round, vote.ProposedBlockInfo.Hash);
        _ = _forensicsProcessor.DetectEquivocationInVotePool(vote, roundVotes);
        _ = _forensicsProcessor.ProcessVoteEquivocation(vote);

        //TODO Optimize this by fetching with block number and round only
        XdcBlockHeader proposedHeader = _tree.FindHeader(vote.ProposedBlockInfo.Hash) as XdcBlockHeader;
        if (proposedHeader is null)
        {
            //This is a vote for a block we have not seen yet, just return for now
            return Task.CompletedTask;
        }

        EpochSwitchInfo epochInfo = _epochSwitchManager.GetEpochSwitchInfo(proposedHeader, vote.ProposedBlockInfo.Hash);
        if (epochInfo is null)
        {
            //Unknown epoch switch info, cannot process vote
            return Task.CompletedTask;
        }
        if (epochInfo.Masternodes.Length == 0)
        {
            throw new InvalidOperationException($"Epoch has empty master node list for {vote.ProposedBlockInfo.Hash}");
        }

        double certThreshold =  _specProvider.GetXdcSpec(proposedHeader, vote.ProposedBlockInfo.Round).CertThreshold;
        bool thresholdReached = roundVotes.Count >= epochInfo.Masternodes.Length * certThreshold;
        if (thresholdReached)
        {
            if (!BlockInfoValidator.ValidateBlockInfo(vote.ProposedBlockInfo, proposedHeader)) return Task.CompletedTask;

            if(!EnsureVotesRecovered(roundVotes, epochInfo.Masternodes)) return Task.CompletedTask;

            OnVotePoolThresholdReached(roundVotes, vote, proposedHeader, epochInfo);
        }
        return Task.CompletedTask;
    }

    public void EndRound(ulong round)
    {
        _votePool.EndRound(round);
    }

    public bool VerifyVotingRules(BlockRoundInfo blockInfo, QuorumCertificate qc)
    {
        if (_ctx.CurrentRound <= _ctx.HighestVotedRound)
        {
            return false;
        }

        if (blockInfo.Round != _ctx.CurrentRound)
        {
            return false;
        }

        if (_ctx.LockQC is null)
        {
            return true;
        }

        if (qc.ProposedBlockInfo.Round > _ctx.LockQC.ProposedBlockInfo.Round)
        {
            return true;
        }

        if (!isExtendingFromAncestor(blockInfo, blockInfo, _ctx.LockQC.ProposedBlockInfo))
        {
            return false;
        }

        return true;
    }

    public Task OnReceiveVote(Vote vote)
    {
        var voteBlockNumber = vote.ProposedBlockInfo.BlockNumber;
        var currentBlockNumber = _tree.Head?.Number ?? throw new InvalidOperationException("Failed to get current block number");
        if (Math.Abs(voteBlockNumber - currentBlockNumber) > XdcConstants.MaxBlockDistance)
        {
            // Discarded propagated vote, too far away
            return Task.CompletedTask;
        }

        if (FilterVote(vote))
        {
            //TODO: Broadcast Vote
            return HandleVote(vote);
        }
        return Task.CompletedTask;
    }

    private bool FilterVote(Vote vote)
    {
        if (vote.ProposedBlockInfo.Round < _ctx.CurrentRound) return false;

        Snapshot snapshot = _snapshotManager.GetSnapshotByGapNumber(_tree, vote.GapNumber);
        if (snapshot is null) throw new InvalidOperationException($"Failed to get snapshot by gapNumber={vote.GapNumber}");
        // Verify message signature
        Address signer = _ethereumEcdsa.RecoverVoteSigner(vote);
        vote.Signer = signer;
        return snapshot.NextEpochCandidates.Any(x => x == signer);
    }

    private void OnVotePoolThresholdReached(IEnumerable<Vote> tally, Vote currVote, XdcBlockHeader proposedBlockHeader, EpochSwitchInfo epochInfo)
    {
        Signature[] validSignatures = tally.Select(v=> v.Signature).ToArray();
        double certThreshold = _specProvider.GetXdcSpec(proposedBlockHeader, currVote.ProposedBlockInfo.Round).CertThreshold;

        if (validSignatures.Count() < epochInfo.Masternodes.Length * certThreshold)
            return;

        QuorumCertificate qc = new(currVote.ProposedBlockInfo, validSignatures, currVote.GapNumber);
        // This qc should be processed using CommitCertificate in QuorumCertificateManager
    }

    private bool isExtendingFromAncestor(BlockRoundInfo blockInfo, BlockRoundInfo currentBlockInfo, BlockRoundInfo ancestorBlockInfo)
    {
        long blockNumDiff = currentBlockInfo.BlockNumber - ancestorBlockInfo.BlockNumber;
        var nextBlockHash = currentBlockInfo.Hash;

        for (int i = 0; i < blockNumDiff; i++)
        {
            XdcBlockHeader parentHeader = _tree.FindHeader(nextBlockHash) as XdcBlockHeader;
            if (parentHeader is null)
            {
                return false;
            }
            else
            {
                nextBlockHash = parentHeader.ParentHash;
            }
        }

        return nextBlockHash == ancestorBlockInfo.Hash;
    }

    private bool EnsureVotesRecovered(IEnumerable<Vote> votes, Address[] masternodes)
    {
        bool allValid = true;
        Parallel.ForEach(votes, (v, s) =>
        {
            if (v.Signer is null)
            {
                v.Signer = _ethereumEcdsa.RecoverVoteSigner(v);
            }

            if (!masternodes.Contains(v.Signer))
            {
                allValid = false;
                s.Stop();
            }
        });
        return allValid;
    }

    private void Sign(Vote vote)
    {
        KeccakRlpStream stream = new();
        _voteDecoder.Encode(stream, vote, RlpBehaviors.ForSealing);
        vote.Signature = _signer.Sign(stream.GetValueHash());
        vote.Signer = _signer.Address;
    }
}
