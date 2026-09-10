// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Nethermind.Consensus.Qbft.Bft;
using Nethermind.Core;

namespace Nethermind.Consensus.Qbft.Validators;

/// <summary>
/// The validator set after a block together with the outstanding add/drop votes; a vote passes once
/// more than half of the current validators cast it.
/// </summary>
public sealed class VoteTally
{
    private readonly SortedSet<Address> _validators;
    private readonly Dictionary<Address, HashSet<Address>> _addVotesBySubject;
    private readonly Dictionary<Address, HashSet<Address>> _removeVotesBySubject;

    public VoteTally(IEnumerable<Address> initialValidators)
        : this([.. initialValidators], [], []) { }

    private VoteTally(SortedSet<Address> validators, Dictionary<Address, HashSet<Address>> addVotes, Dictionary<Address, HashSet<Address>> removeVotes)
    {
        _validators = validators;
        _addVotesBySubject = addVotes;
        _removeVotesBySubject = removeVotes;
    }

    /// <summary>Validators in ascending address order.</summary>
    public IReadOnlyList<Address> Validators => _validators.ToArray();

    public void AddVote(ValidatorVote vote)
    {
        HashSet<Address> addVotes = GetOrAdd(_addVotesBySubject, vote.Recipient);
        HashSet<Address> removeVotes = GetOrAdd(_removeVotesBySubject, vote.Recipient);
        if (vote.IsAuthVote)
        {
            addVotes.Add(vote.Proposer);
            removeVotes.Remove(vote.Proposer);
        }
        else
        {
            removeVotes.Add(vote.Proposer);
            addVotes.Remove(vote.Proposer);
        }

        int limit = ValidatorLimit;
        if (addVotes.Count >= limit)
        {
            _validators.Add(vote.Recipient);
            DiscardOutstandingVotesFor(vote.Recipient);
        }

        if (removeVotes.Count >= limit)
        {
            _validators.Remove(vote.Recipient);
            DiscardOutstandingVotesFor(vote.Recipient);
            foreach (HashSet<Address> votes in _addVotesBySubject.Values) votes.Remove(vote.Recipient);
            foreach (HashSet<Address> votes in _removeVotesBySubject.Values) votes.Remove(vote.Recipient);
        }
    }

    public IReadOnlySet<Address> GetOutstandingAddVotesFor(Address subject) =>
        _addVotesBySubject.TryGetValue(subject, out HashSet<Address>? votes) ? votes : [];

    public IReadOnlySet<Address> GetOutstandingRemoveVotesFor(Address subject) =>
        _removeVotesBySubject.TryGetValue(subject, out HashSet<Address>? votes) ? votes : [];

    public void DiscardOutstandingVotes()
    {
        _addVotesBySubject.Clear();
        _removeVotesBySubject.Clear();
    }

    public VoteTally Copy() => new(
        [.. _validators],
        _addVotesBySubject.ToDictionary(static kv => kv.Key, static kv => new HashSet<Address>(kv.Value)),
        _removeVotesBySubject.ToDictionary(static kv => kv.Key, static kv => new HashSet<Address>(kv.Value)));

    private int ValidatorLimit => _validators.Count / 2 + 1;

    private void DiscardOutstandingVotesFor(Address subject)
    {
        _addVotesBySubject.Remove(subject);
        _removeVotesBySubject.Remove(subject);
    }

    private static HashSet<Address> GetOrAdd(Dictionary<Address, HashSet<Address>> votes, Address subject)
    {
        if (!votes.TryGetValue(subject, out HashSet<Address>? set))
        {
            set = [];
            votes[subject] = set;
        }

        return set;
    }
}
