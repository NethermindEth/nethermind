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
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Nethermind.Xdc;
internal class VotesManager (
    XdcContext context,
    IBlockTree tree,
    IEpochSwitchManager epochSwitchManager,
    ISpecProvider specProvider,
    ISigner signer,
    IForensicsProcessor forensicsProcessor) : IVotesManager
{
    private IBlockTree _tree = tree;
    private IEpochSwitchManager _epochSwitchManager = epochSwitchManager;
    private XdcContext _ctx = context;
    private IForensicsProcessor _forensicsProcessor = forensicsProcessor;
    private ISpecProvider _specProvider = specProvider;
    private ISigner _signer = signer;

    private VotePool _votePool => new();
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
        Sign(vote);

        _ctx.HighestVotedRound = blockInfo.Round;

        HandleVote(vote);
        //TODO Broadcast vote to peers
        return Task.CompletedTask;
    }

    private void Sign(Vote vote)
    {
        KeccakRlpStream stream = new();
        _voteDecoder.Encode(stream, vote, RlpBehaviors.ForSealing);
        vote.Signature = _signer.Sign(stream.GetValueHash());
    }

    public Task HandleVote(Vote vote)
    {
        if ((vote.ProposedBlockInfo.Round != _ctx.CurrentRound) && (vote.ProposedBlockInfo.Round != _ctx.CurrentRound + 1))
        {
            //We only care about votes for the current round or the next round
            return Task.CompletedTask;
        }

        EpochSwitchInfo epochInfo = _epochSwitchManager.GetEpochSwitchInfo(null, vote.ProposedBlockInfo.Hash);
        if (epochInfo is null)
        {
            //Unknown epoch switch info, cannot process vote
            return Task.CompletedTask;
        }
        if (epochInfo.Masternodes.Length == 0)
        {
            throw new InvalidOperationException($"Epoch has empty master node list for {vote.ProposedBlockInfo.Hash}");
        }

        if (!ValidateVote(vote, epochInfo))
        {
            return Task.CompletedTask;
        }
        //TODO check for duplicate votes from the same signer
        _votePool.Add(vote);
        IReadOnlyCollection<Vote> roundVotes = _votePool.GetRoundVotes(vote.ProposedBlockInfo.Round);
        _ = _forensicsProcessor.DetectEquivocationInVotePool(vote, roundVotes);
        _ = _forensicsProcessor.ProcessVoteEquivocation(vote);

        if (vote.ProposedBlockInfo.Round == _ctx.CurrentRound + 1)
        {
            //This vote is for the next round, so no need to go further
            return Task.CompletedTask;
        }

        //TODO Optimize this by fetching with block number and round only
        XdcBlockHeader proposedHeader = _tree.FindHeader(vote.ProposedBlockInfo.Hash) as XdcBlockHeader;
        if (proposedHeader is null)
        {
            //This is a vote for a block we have not seen yet, just return for now
            return Task.CompletedTask;
        }
        IXdcReleaseSpec spec = _specProvider.GetXdcSpec(proposedHeader, vote.ProposedBlockInfo.Round);
        double certThreshold =  spec.CertThreshold;
        bool thresholdReached = roundVotes.Count >= epochInfo.Masternodes.Length * certThreshold;

        if (thresholdReached)
        {
            EnsureVotesRecovered(roundVotes, proposedHeader);

            OnVotePoolThresholdReached(roundVotes, vote, proposedHeader, epochInfo);
        }
        return Task.CompletedTask;
    }

    public void EndRound(ulong round)
    {
        _votePool.EndRoundVote(round);
    }

    private bool ValidateVote(Vote vote, EpochSwitchInfo epochInfo)
    {
        if (vote.Signer is null)
        {
            vote.Signer = _ethereumEcdsa.RecoverVoteSigner(vote);
            if (vote.Signer is null) return false;
        }

        return epochInfo.Masternodes.Any(masternode => masternode == vote.Signer);
    }

    private void OnVotePoolThresholdReached(IEnumerable<Vote> tally, Vote currVote, XdcBlockHeader proposedBlockHeader, EpochSwitchInfo epochInfo)
    {
        //Make sure only one thread can enter and the event is only fired once per round vote
        Signature[] validSignature = tally.Select(v=>v.Signature).ToArray();
        IXdcReleaseSpec spec = _specProvider.GetXdcSpec(proposedBlockHeader, currVote.ProposedBlockInfo.Round);
        double certThreshold = spec.CertThreshold;

        if (validSignature.Count() < epochInfo.Masternodes.Length * certThreshold)
            return;

        //Invoke event here
        QuorumCertificate qc = new(currVote.ProposedBlockInfo, validSignature, currVote.GapNumber);
    }

    private void EnsureVotesRecovered(IEnumerable<Vote> votes, XdcBlockHeader header)
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
        if (_ctx.CurrentRound <= _ctx.HighestVotedRound)
        {
            return false;
        }

        if (blockInfo.Round != _ctx.CurrentRound)
        {
            return true;
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

        return true;
    }

    private bool isExtendingFromAncestor(BlockRoundInfo blockInfo, BlockRoundInfo currentBlockInfo, BlockRoundInfo ancestorBlockInfo)
    {
        long blockNumDiff = currentBlockInfo.BlockNumber - ancestorBlockInfo.BlockNumber;

        var nextBlockHash = currentBlockInfo.Hash;

        XdcBlockHeader parentHeader = default;

        for (int i = 0; i < blockNumDiff; i++)
        {
            parentHeader = _tree.FindHeader(nextBlockHash) as XdcBlockHeader;
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

    private class VotePool
    {
        private readonly Dictionary<ulong, ArrayPoolList<Vote>> _votes = new();
        private readonly McsLock _lock = new();

        public void Add(Vote vote)
        {
            using var lockRelease = _lock.Acquire();
            {
                if (!_votes.TryGetValue(vote.ProposedBlockInfo.Round, out var list))
                {
                    //128 should be enough to cover all master nodes and some extras
                    list = new ArrayPoolList<Vote>(128);
                    _votes[vote.ProposedBlockInfo.Round] = list;
                }
                list.Add(vote);
            }
        }

        public void EndRoundVote(ulong round)
        {
            using var lockRelease = _lock.Acquire();
            {
                foreach (var key in _votes.Keys)
                {
                    if (key <= round && _votes.Remove(key, out ArrayPoolList<Vote> list))
                    {
                        list?.Dispose();
                    }
                }
            }
        }

        public IReadOnlyCollection<Vote> GetRoundVotes(ulong round)
        {
            using var lockRelease = _lock.Acquire();
            {
                if (_votes.TryGetValue(round, out ArrayPoolList<Vote> list))
                {
                    //Allocating a new array since it goes outside of the lock
                    return list.ToArray();
                }
                return [];
            }
        }

        public long GetRoundCount(ulong round)
        {
            using var lockRelease = _lock.Acquire();
            {
                if (_votes.TryGetValue(round, out ArrayPoolList<Vote> list))
                {
                    return list.Count;
                }
                return 0;
            }
        }
    }
}

public class VoteThresholdArgs(QuorumCertificate qc, long round)
{
    public QuorumCertificate QuorumCertificate { get; } = qc;
    public long Round { get; } = round;
}
