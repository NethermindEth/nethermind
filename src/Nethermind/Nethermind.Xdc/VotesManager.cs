// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Serialization.Rlp;
using Nethermind.Xdc.Spec;
using Nethermind.Xdc.Types;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Nethermind.Xdc;

internal class VotesManager(
    IXdcConsensusContext context,
    IBlockTree tree,
    IEpochSwitchManager epochSwitchManager,
    ISnapshotManager snapshotManager,
    IQuorumCertificateManager quorumCertificateManager,
    ISpecProvider specProvider,
    ISigner signer,
    IForensicsProcessor forensicsProcessor) : IVotesManager
{
    private readonly IBlockTree _blockTree = tree;
    private readonly IEpochSwitchManager _epochSwitchManager = epochSwitchManager;
    private readonly ISnapshotManager _snapshotManager = snapshotManager;
    private readonly IQuorumCertificateManager _quorumCertificateManager = quorumCertificateManager;
    private readonly IXdcConsensusContext _ctx = context;
    private readonly IForensicsProcessor _forensicsProcessor = forensicsProcessor;
    private readonly ISpecProvider _specProvider = specProvider;
    private readonly ISigner _signer = signer;

    private readonly XdcPool<Vote> _votePool = new();
    private static readonly VoteDecoder _voteDecoder = new();
    private static readonly EthereumEcdsa _ethereumEcdsa = new(0);
    private readonly ConcurrentDictionary<ulong, byte> _qcBuildStartedByRound = new();
    private const int _maxBlockDistance = 7; // Maximum allowed backward distance from the chain head
    private long _highestVotedRound = -1;

    public Task CastVote(BlockRoundInfo blockInfo)
    {
        EpochSwitchInfo epochSwitchInfo = _epochSwitchManager.GetEpochSwitchInfo(blockInfo.Hash);
        if (epochSwitchInfo is null)
            throw new ArgumentException($"Cannot find epoch info for block {blockInfo.Hash}", nameof(EpochSwitchInfo));
        //Optimize this by fetching with block number and round only

        XdcBlockHeader header = _blockTree.FindHeader(blockInfo.Hash) as XdcBlockHeader;
        if (header is null)
            throw new ArgumentException($"Cannot find block header for block {blockInfo.Hash}");

        IXdcReleaseSpec spec = _specProvider.GetXdcSpec(header, blockInfo.Round);
        long epochSwitchNumber = epochSwitchInfo.EpochSwitchBlockInfo.BlockNumber;
        long gapNumber = epochSwitchNumber == 0 ? 0 : Math.Max(0, epochSwitchNumber - epochSwitchNumber % spec.EpochLength - spec.Gap);

        var vote = new Vote(blockInfo, (ulong)gapNumber);
        // Sets signature and signer for the vote
        Sign(vote);

        _highestVotedRound = (long)blockInfo.Round;

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
        _votePool.Add(vote);
        IReadOnlyCollection<Vote> roundVotes = _votePool.GetItems(vote);
        _ = _forensicsProcessor.DetectEquivocationInVotePool(vote, roundVotes);
        _ = _forensicsProcessor.ProcessVoteEquivocation(vote);

        XdcBlockHeader proposedHeader = _blockTree.FindHeader(vote.ProposedBlockInfo.Hash, vote.ProposedBlockInfo.BlockNumber) as XdcBlockHeader;
        if (proposedHeader is null)
        {
            //This is a vote for a block we have not seen yet, just return for now
            return Task.CompletedTask;
        }

        EpochSwitchInfo epochInfo = _epochSwitchManager.GetEpochSwitchInfo(proposedHeader);
        if (epochInfo is null)
        {
            //Unknown epoch switch info, cannot process vote
            return Task.CompletedTask;
        }
        if (epochInfo.Masternodes.Length == 0)
        {
            throw new InvalidOperationException($"Epoch has empty master node list for {vote.ProposedBlockInfo.Hash}");
        }

        double certThreshold = _specProvider.GetXdcSpec(proposedHeader, vote.ProposedBlockInfo.Round).CertThreshold;
        bool thresholdReached = roundVotes.Count >= epochInfo.Masternodes.Length * certThreshold;
        if (thresholdReached)
        {
            if (!vote.ProposedBlockInfo.ValidateBlockInfo(proposedHeader))
                return Task.CompletedTask;

            Signature[] validSignatures = GetValidSignatures(roundVotes, epochInfo.Masternodes);
            if (validSignatures.Length < epochInfo.Masternodes.Length * certThreshold)
                return Task.CompletedTask;

            // At this point, the QC should be processed for this *round*.
            // Ensure this runs only once per round:
            var round = vote.ProposedBlockInfo.Round;
            if (!_qcBuildStartedByRound.TryAdd(round, 0))
                return Task.CompletedTask;
            OnVotePoolThresholdReached(validSignatures, vote);
        }
        return Task.CompletedTask;
    }

    private void EndRound(ulong round)
    {
        _votePool.EndRound(round);

        foreach (var key in _qcBuildStartedByRound.Keys)
            if (key <= round) _qcBuildStartedByRound.TryRemove(key, out _);
    }

    public bool VerifyVotingRules(BlockRoundInfo roundInfo, QuorumCertificate qc) => VerifyVotingRules(roundInfo.Hash, roundInfo.BlockNumber, roundInfo.Round, qc);
    public bool VerifyVotingRules(XdcBlockHeader header) => VerifyVotingRules(header.Hash, header.Number, header.ExtraConsensusData.BlockRound, header.ExtraConsensusData.QuorumCert);
    public bool VerifyVotingRules(Hash256 blockHash, long blockNumber, ulong roundNumber, QuorumCertificate qc)
    {
        if ((long)_ctx.CurrentRound <= _highestVotedRound)
        {
            return false;
        }

        if (roundNumber != _ctx.CurrentRound)
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

        if (!IsExtendingFromAncestor(blockHash, blockNumber, _ctx.LockQC.ProposedBlockInfo))
        {
            return false;
        }

        return true;
    }

    public Task OnReceiveVote(Vote vote)
    {
        var voteBlockNumber = vote.ProposedBlockInfo.BlockNumber;
        var currentBlockNumber = _blockTree.Head?.Number ?? throw new InvalidOperationException("Failed to get current block number");
        if (Math.Abs(voteBlockNumber - currentBlockNumber) > _maxBlockDistance)
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

    internal bool FilterVote(Vote vote)
    {
        if (vote.ProposedBlockInfo.Round < _ctx.CurrentRound) return false;

        Snapshot snapshot = _snapshotManager.GetSnapshotByGapNumber((long)vote.GapNumber);
        if (snapshot is null) return false;
        // Verify message signature
        vote.Signer ??= _ethereumEcdsa.RecoverVoteSigner(vote);
        return snapshot.NextEpochCandidates.Any(x => x == vote.Signer);
    }

    private void OnVotePoolThresholdReached(Signature[] validSignatures, Vote currVote)
    {
        QuorumCertificate qc = new(currVote.ProposedBlockInfo, validSignatures, currVote.GapNumber);
        _quorumCertificateManager.CommitCertificate(qc);
        EndRound(currVote.ProposedBlockInfo.Round);
    }

    private bool IsExtendingFromAncestor(Hash256 blockHash, long blockNumber, BlockRoundInfo ancestorBlockInfo)
    {
        long blockNumDiff = blockNumber - ancestorBlockInfo.BlockNumber;
        Hash256 nextBlockHash = blockHash;

        for (int i = 0; i < blockNumDiff; i++)
        {
            XdcBlockHeader parentHeader = _blockTree.FindHeader(nextBlockHash) as XdcBlockHeader;
            if (parentHeader is null)
                return false;

            nextBlockHash = parentHeader.ParentHash;
        }

        return nextBlockHash == ancestorBlockInfo.Hash;
    }

    private Signature[] GetValidSignatures(IEnumerable<Vote> votes, Address[] masternodes)
    {
        var masternodeSet = new HashSet<Address>(masternodes);
        var signatures = new List<Signature>();
        foreach (var vote in votes)
        {
            if (vote.Signer is null)
            {
                vote.Signer = _ethereumEcdsa.RecoverVoteSigner(vote);
            }

            if (masternodeSet.Contains(vote.Signer))
            {
                signatures.Add(vote.Signature);
            }
        }
        return signatures.ToArray();
    }

    private void Sign(Vote vote)
    {
        KeccakRlpStream stream = new();
        _voteDecoder.Encode(stream, vote, RlpBehaviors.ForSealing);
        vote.Signature = _signer.Sign(stream.GetValueHash());
        vote.Signer = _signer.Address;
    }

    public long GetVotesCount(Vote vote)
    {
        return _votePool.GetCount(vote);
    }
}
