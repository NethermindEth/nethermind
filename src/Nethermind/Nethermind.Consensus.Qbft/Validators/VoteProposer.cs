// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Nethermind.Consensus.Qbft.Bft;
using Nethermind.Core;

namespace Nethermind.Consensus.Qbft.Validators;

/// <summary>The local node's pending membership proposals, cast round-robin one per proposed block.</summary>
public sealed class VoteProposer
{
    private readonly ConcurrentDictionary<Address, VoteType> _proposals = new();
    private int _votePosition;

    public void Auth(Address address) => _proposals[address] = VoteType.Add;
    public void Drop(Address address) => _proposals[address] = VoteType.Drop;
    public void Discard(Address address) => _proposals.TryRemove(address, out _);

    public IReadOnlyDictionary<Address, VoteType> GetProposals() => new Dictionary<Address, VoteType>(_proposals);

    /// <summary>The next proposal that still needs the local node's vote given the current tally, if any.</summary>
    public ValidatorVote? GetVote(Address localAddress, VoteTally tally)
    {
        IReadOnlyList<Address> validators = tally.Validators;
        List<KeyValuePair<Address, VoteType>> validVotes = [];
        foreach (KeyValuePair<Address, VoteType> proposal in _proposals)
        {
            if (VoteNotYetCast(localAddress, proposal.Key, proposal.Value, validators, tally))
            {
                validVotes.Add(proposal);
            }
        }

        if (validVotes.Count == 0)
        {
            return null;
        }

        int position = Interlocked.Increment(ref _votePosition) % validVotes.Count;
        KeyValuePair<Address, VoteType> voteToCast = validVotes[position];
        return new ValidatorVote(voteToCast.Value, localAddress, voteToCast.Key);
    }

    private static bool VoteNotYetCast(Address localAddress, Address subject, VoteType vote, IReadOnlyList<Address> validators, VoteTally tally)
    {
        bool votedAuth = tally.GetOutstandingAddVotesFor(subject).Contains(localAddress);
        bool votedDrop = tally.GetOutstandingRemoveVotesFor(subject).Contains(localAddress);
        bool isValidator = validators.ContainsAddress(subject);

        return vote switch
        {
            VoteType.Drop when isValidator && !votedDrop => true,
            VoteType.Drop when votedAuth => true,
            VoteType.Add when !isValidator && !votedAuth => true,
            VoteType.Add when votedDrop => true,
            _ => false,
        };
    }
}
