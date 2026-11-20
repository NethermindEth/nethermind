// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Immutable;
using System.Numerics;
using Nethermind.BeaconChain.Spec;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Serialization.Ssz.Merkleization;

namespace Nethermind.BeaconChain.Types;

/// <summary>Shares immutable vote histories across state branches until a caller requests the mutable SSZ array.</summary>
internal struct Eth1VoteList(Eth1Data[]? array)
{
    private Eth1Data[]? _array = array;
    private History? _history;

    public Eth1Data[]? AsArray()
    {
        if (_array is null && _history is { } history)
        {
            Eth1Data[] votes = new Eth1Data[history.Count];
            int i = votes.Length;
            foreach (Vote vote in history.Votes)
                votes[--i] = vote.ToData();
            Interlocked.CompareExchange(ref _array, votes, null);
        }
        return _array;
    }

    public Eth1VoteList Clone()
    {
        Eth1Data[]? exposed = Volatile.Read(ref _array);
        return exposed switch
        {
            null => new Eth1VoteList { _history = _history },
            { Length: 0 } => new(exposed),
            _ => new Eth1VoteList { _history = Snapshot(exposed) },
        };
    }

    private static History Snapshot(Eth1Data[] exposed)
    {
        History history = History.Empty;
        foreach (Eth1Data vote in exposed)
            history = history.Append(vote, out _);
        return history;
    }

    public int Append(Eth1Data data)
    {
        History previous = _array is null ? _history ?? History.Empty : Snapshot(_array);
        _history = previous.Append(data, out int votes);
        _array = null;
        return votes;
    }

    public readonly UInt256 HashTreeRoot()
    {
        if (_array is null && _history is { } history)
            return history.Root;
        Eth1Data.MerkleizeList(_array ?? [], Presets.Eth1DataVotesLimit, out UInt256 root);
        return root;
    }

    private readonly record struct Vote(ValueHash256? DepositRoot, ulong DepositCount, ValueHash256? BlockHash) : IComparable<Vote>
    {
        public int CompareTo(Vote other)
        {
            int order = DepositCount.CompareTo(other.DepositCount);
            if (order == 0)
                order = Nullable.Compare(DepositRoot, other.DepositRoot);
            return order == 0 ? Nullable.Compare(BlockHash, other.BlockHash) : order;
        }

        public Eth1Data ToData() => new()
        {
            DepositRoot = DepositRoot is { } deposit ? new Hash256(deposit) : null,
            DepositCount = DepositCount,
            BlockHash = BlockHash is { } block ? new Hash256(block) : null,
        };
    }

    private sealed record History(int Count, ImmutableStack<Vote> Votes, ImmutableSortedDictionary<Vote, int> Counts, UInt256[] Frontier, UInt256 Root)
    {
        private static readonly int Depth = BitOperations.Log2(Presets.Eth1DataVotesLimit);
        public static readonly History Empty = new(0, ImmutableStack<Vote>.Empty, ImmutableSortedDictionary<Vote, int>.Empty, new UInt256[Depth], default);

        public History Append(Eth1Data data, out int matches)
        {
            if ((ulong)Count >= Presets.Eth1DataVotesLimit)
                throw new InvalidOperationException("Eth1 voting period is full.");

            Vote vote = new(data.DepositRoot?.ValueHash256, data.DepositCount, data.BlockHash?.ValueHash256);
            Counts.TryGetValue(vote, out matches);
            matches++;
            Eth1Data.Merkleize(data, out UInt256 root);
            UInt256[] frontier = (UInt256[])Frontier.Clone();
            int index = Count;
            // Only the first missing sibling completes a new frontier entry. Higher roots include padding.
            int completedLevel = BitOperations.TrailingZeroCount(~Count);
            for (int level = 0; level < Depth; level++, index >>= 1)
            {
                if ((index & 1) != 0)
                    Merkle.Merkleize(out root, [frontier[level], root]);
                else
                {
                    if (level == completedLevel)
                        frontier[level] = root;
                    Merkle.Merkleize(out root, [root, Merkle.ZeroHashes[level]]);
                }
            }
            Merkle.MixIn(ref root, Count + 1);
            return new(Count + 1, Votes.Push(vote), Counts.SetItem(vote, matches), frontier, root);
        }
    }
}
