// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Synchronization.Peers;
using Nethermind.Xdc.P2P;
using Nethermind.Core.Specs;
using Nethermind.Xdc.Spec;
using Nethermind.Xdc.Types;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Xdc.RLP;

namespace Nethermind.Xdc;

internal class VotesManager(
    IXdcConsensusContext context,
    ISyncPeerPool syncPeerPool,
    IBlockTree tree,
    IEpochSwitchManager epochSwitchManager,
    ISnapshotManager snapshotManager,
    IQuorumCertificateManager quorumCertificateManager,
    ISpecProvider specProvider,
    ISigner signer,
    IForensicsProcessor forensicsProcessor,
    ILogManager logManager) : IVotesManager
{
    private readonly IBlockTree _blockTree = tree;
    private readonly IEpochSwitchManager _epochSwitchManager = epochSwitchManager;
    private readonly ISnapshotManager _snapshotManager = snapshotManager;
    private readonly IQuorumCertificateManager _quorumCertificateManager = quorumCertificateManager;
    private readonly IXdcConsensusContext _ctx = context;
    private readonly ISyncPeerPool _syncPeerPool = syncPeerPool;
    private readonly IForensicsProcessor _forensicsProcessor = forensicsProcessor;
    private readonly ISpecProvider _specProvider = specProvider;
    private readonly ISigner _signer = signer;
    private readonly ILogger _logger = logManager.GetClassLogger<VotesManager>();

    private readonly XdcPool<Vote> _votePool = new();
    private static readonly VoteDecoder _voteDecoder = new();
    private static readonly EthereumEcdsa _ethereumEcdsa = new(0);
    private readonly ConcurrentDictionary<ulong, byte> _qcBuildStartedByRound = new();
    private const int _maxBlockDistance = 7; // Maximum allowed backward distance from the chain head

    // null means "never voted"; ulong.MaxValue is a valid round so null is the correct sentinel.
    private ulong? _highestVotedRound = null;

    public Task CastVote(BlockRoundInfo blockInfo)
    {
        EpochSwitchInfo epochSwitchInfo = _epochSwitchManager.GetEpochSwitchInfo(blockInfo.Hash) ??
            throw new ArgumentException($"Cannot find epoch info for block {blockInfo.Hash}", nameof(blockInfo));

        if (_blockTree.FindHeader(blockInfo.Hash) is not XdcBlockHeader header)
            throw new ArgumentException($"Cannot find block header for block {blockInfo.Hash}");

        IXdcReleaseSpec spec = _specProvider.GetXdcSpec(header, blockInfo.Round);
        ulong epochSwitchNumber = epochSwitchInfo.EpochSwitchBlockInfo.BlockNumber;

        ulong gapNumber;
        if (epochSwitchNumber == 0)
        {
            gapNumber = 0;
        }
        else
        {
            ulong offset = epochSwitchNumber % spec.EpochLength + spec.Gap;
            gapNumber = epochSwitchNumber.SaturatingSub(offset);
        }

        Vote vote = new(blockInfo, gapNumber, isMyVote: true);
        // Sets signature and signer for the vote
        if (!TrySign(vote))
        {
            if (_logger.IsWarn) _logger.Warn($"XDC signer {_signer.Address} could not sign vote for block {blockInfo.Hash} (round {blockInfo.Round}) — skipping broadcast.");
            return Task.CompletedTask;
        }

        _highestVotedRound = blockInfo.Round;

        HandleVote(vote);
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
        IReadOnlyCollection<Vote> roundVotes = _votePool.GetItemsByKey(vote);
        IReadOnlyCollection<Vote> roundVotesFromOtherKeys = _votePool.GetItemsFromRoundExcludingKey(vote);
        // Forensics is expected to run asynchronously and must not block vote processing.
        // The two calls are complementary: one checks signer conflicts across pool keys in the same round,
        // the other validates against committed-QC ancestry.
        _ = _forensicsProcessor.DetectEquivocationInVotePool(vote, roundVotesFromOtherKeys);
        _ = _forensicsProcessor.ProcessVoteEquivocation(vote);

        if (_blockTree.FindHeader(vote.ProposedBlockInfo.Hash, vote.ProposedBlockInfo.BlockNumber) is not XdcBlockHeader proposedHeader)
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
        int masternodeCount = epochInfo.Masternodes.Length;
        if (masternodeCount == 0)
        {
            throw new InvalidOperationException($"Epoch has empty master node list for {vote.ProposedBlockInfo.Hash}");
        }

        BroadcastVote(vote);

        double certThreshold = _specProvider.GetXdcSpec(proposedHeader, vote.ProposedBlockInfo.Round).CertificateThreshold;
        double requiredVotes = masternodeCount * certThreshold;
        bool thresholdReached = roundVotes.Count >= requiredVotes;
        if (thresholdReached)
        {
            if (!vote.ProposedBlockInfo.ValidateBlockInfo(proposedHeader))
                return Task.CompletedTask;

            Signature[] validSignatures = GetValidSignatures(roundVotes, epochInfo.Masternodes);
            if (validSignatures.Length < requiredVotes)
                return Task.CompletedTask;

            // At this point, the QC should be processed for this *round*.
            // Ensure this runs only once per round:
            ulong round = vote.ProposedBlockInfo.Round;
            if (!_qcBuildStartedByRound.TryAdd(round, 0))
                return Task.CompletedTask;
            OnVotePoolThresholdReached(validSignatures, vote);
        }
        return Task.CompletedTask;
    }

    private void CleanupVotes(ulong round)
    {
        _votePool.EndRound(round);

        foreach (KeyValuePair<ulong, byte> kvp in _qcBuildStartedByRound)
            if (kvp.Key <= round) _qcBuildStartedByRound.TryRemove(kvp.Key, out _);
    }

    public bool VerifyVotingRules(BlockRoundInfo roundInfo, QuorumCertificate qc, out string? error) =>
        VerifyVotingRules(roundInfo.Hash, roundInfo.BlockNumber, roundInfo.Round, qc, out error);

    public bool VerifyVotingRules(XdcBlockHeader header, [NotNullWhen(false)] out string? error) =>
        VerifyVotingRules(header.Hash, header.Number, header.ExtraConsensusData.BlockRound, header.ExtraConsensusData.QuorumCert, out error);

    public bool VerifyVotingRules(Hash256 blockHash, ulong blockNumber, ulong roundNumber, QuorumCertificate qc, out string? error)
    {
        // _highestVotedRound is null until a vote is cast; once set, any round <= it is rejected.
        if (_highestVotedRound.HasValue && _ctx.CurrentRound <= _highestVotedRound.Value)
        {
            error = $"Already voted at round {_highestVotedRound.Value}, current round {_ctx.CurrentRound}";
            return false;
        }

        if (roundNumber != _ctx.CurrentRound)
        {
            error = $"Vote round {roundNumber} does not match current round {_ctx.CurrentRound}";
            return false;
        }

        if (_ctx.LockQC is null)
        {
            error = null;
            return true;
        }

        if (qc.ProposedBlockInfo.Round > _ctx.LockQC.ProposedBlockInfo.Round)
        {
            error = null;
            return true;
        }

        BlockRoundInfo locked = _ctx.LockQC.ProposedBlockInfo;
        if (!IsExtendingFromAncestor(blockHash, blockNumber, locked))
        {
            error =
                $"Block {blockHash} (number {blockNumber}, round {roundNumber}) does not extend from locked QC block " +
                $"{locked.Hash}(number {locked.BlockNumber}, round {locked.Round})";
            return false;
        }

        error = null;
        return true;
    }

    public Task OnReceiveVote(Vote vote)
    {
        ulong voteBlockNumber = vote.ProposedBlockInfo.BlockNumber;
        ulong currentBlockNumber = _blockTree.Head?.Number ?? throw new InvalidOperationException("Failed to get current block number");

        ulong blockDiff = voteBlockNumber > currentBlockNumber
            ? voteBlockNumber - currentBlockNumber
            : currentBlockNumber - voteBlockNumber;

        if (blockDiff > _maxBlockDistance)
        {
            // Discarded propagated vote, too far away
            return Task.CompletedTask;
        }

        if (FilterVote(vote))
        {
            return HandleVote(vote);
        }
        return Task.CompletedTask;
    }

    internal bool FilterVote(Vote vote)
    {
        if (vote.ProposedBlockInfo.Round < _ctx.CurrentRound) return false;

        Snapshot snapshot = _snapshotManager.GetSnapshotByGapNumber(vote.GapNumber);
        if (snapshot is null) return false;
        // Verify message signature
        vote.Signer ??= _ethereumEcdsa.RecoverVoteSigner(vote);
        return snapshot.NextEpochCandidates.Any(x => x == vote.Signer);
    }

    private void BroadcastVote(Vote vote)
    {
        foreach (PeerInfo peer in _syncPeerPool.AllPeers)
        {
            if (peer.SyncPeer is XdcProtocolHandler xdcProtocol)
                xdcProtocol.SendVote(vote);
        }
    }

    private void OnVotePoolThresholdReached(Signature[] validSignatures, Vote currVote)
    {
        QuorumCertificate qc = new(currVote.ProposedBlockInfo, validSignatures, currVote.GapNumber);
        _quorumCertificateManager.CommitCertificate(qc);
        CleanupVotes(currVote.ProposedBlockInfo.Round);
    }

    private bool IsExtendingFromAncestor(Hash256 blockHash, ulong blockNumber, BlockRoundInfo ancestorBlockInfo)
    {
        if (blockNumber < ancestorBlockInfo.BlockNumber)
            return false;

        ulong blockNumDiff = blockNumber - ancestorBlockInfo.BlockNumber;
        Hash256 nextBlockHash = blockHash;

        for (ulong i = 0; i < blockNumDiff; i++)
        {
            if (_blockTree.FindHeader(nextBlockHash) is not XdcBlockHeader parentHeader)
                return false;

            nextBlockHash = parentHeader.ParentHash;
        }

        return nextBlockHash == ancestorBlockInfo.Hash;
    }

    private static Signature[] GetValidSignatures(IEnumerable<Vote> votes, Address[] masternodes)
    {
        HashSet<Address> masternodeSet = [.. masternodes];
        List<Signature> signatures = [];
        foreach (Vote vote in votes)
        {
            vote.Signer ??= _ethereumEcdsa.RecoverVoteSigner(vote);

            if (masternodeSet.Contains(vote.Signer))
            {
                signatures.Add(vote.Signature);
            }
        }
        return signatures.ToArray();
    }

    /// <summary>
    /// Verifies each signature against <paramref name="allowedSigners"/>, deduplicates by
    /// recovered address, and returns the number of distinct valid signers.
    /// </summary>
    /// <returns>
    /// Signatures count if all are valid, or <c>null</c>
    /// if there's any validation <paramref name="error"/>.
    /// </returns>
    public static int? CountValidSignatures(
        IReadOnlyCollection<Address> allowedSigners,
        IReadOnlyCollection<Signature> signatures,
        ValueHash256 messageHash,
        out string? error)
    {
        //TODO: try to minimize number of allocations, at least for common cases
        Dictionary<Address, int> signedBy = new(allowedSigners.Count);
        foreach (Address signer in allowedSigners)
            signedBy.TryAdd(signer, 0);

        int count = 0;
        string? localError = null; // concurrent "overwrite" is ok, no need to synchronize
        Parallel.ForEach(signatures, (s, state) =>
        {
            Address signer = _ethereumEcdsa.RecoverAddress(s, messageHash);
            ref int signCount = ref CollectionsMarshal.GetValueRefOrNullRef(signedBy, signer);

            if (Unsafe.IsNullRef(ref signCount))
            {
                localError = "Certificate contains an invalid signature";
                state.Stop();
                return;
            }

            if (Interlocked.Increment(ref signCount) != 1)
            {
                localError = $"Certificate contains a duplicate signature from {signer}";
                state.Stop();
                return;
            }

            Interlocked.Increment(ref count);
        });

        error = localError;
        return error is null ? count : null;
    }

    private bool TrySign(Vote vote)
    {
        KeccakRlpWriter writer = new();
        _voteDecoder.Encode(ref writer, vote, RlpBehaviors.ForSealing);
        ValueHash256 hash = writer.GetValueHash();
        if (!_signer.TrySign(in hash, out Signature signature))
            return false;
        vote.Signature = signature;
        vote.Signer = _signer.Address;
        return true;
    }

    public long GetVotesCount(Vote vote) => _votePool.GetCount(vote);

    public IDictionary<(ulong Round, Hash256 Hash), Dictionary<Address, Vote>> GetReceivedVotes() => _votePool.GetItems();
}
