// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.ForkChoice;
using Nethermind.Core.Crypto;
using static Nethermind.BeaconChain.Test.ForkChoice.TestHashes;

namespace Nethermind.BeaconChain.Test.ForkChoice;

/// <summary>The independent model combines vote deltas and proposer boost in one backward pass.</summary>
public class ProtoArrayBoostPassDifferentialTests
{
    private const ulong Gwei32Eth = 32_000_000_000;
    private const ulong SlotsPerEpoch = 32;

    private static readonly CheckpointRef Anchor = new(0, GetRoot(0));

    private static IEnumerable<TestCaseData> Scenarios()
    {
        yield return new TestCaseData((Action<Driver>)ParentCollectsTwoCommittees).SetName("Fixture mirror: parent collects two committees under a boost");
        yield return new TestCaseData((Action<Driver>)VotesAndBalancesMove).SetName("Votes and balances move between branches");
        yield return new TestCaseData((Action<Driver>)BoostMovesAcrossBranchesAndInvalidation).SetName("Boost set, moved, kept on an invalidated branch and reset");
        yield return new TestCaseData((Action<Driver>)PruneBetweenUpdates).SetName("Prune between updates with a boost across it");
        yield return new TestCaseData((Action<Driver>)EquivocationUnderBoost).SetName("Equivocation under a boost");
        yield return new TestCaseData((Action<Driver>)BoostUnderAnInvalidAncestor).SetName("Boost on an optimistic block under an invalid ancestor");
    }

    [TestCaseSource(nameof(Scenarios))]
    public void Weights_match_the_single_pass_reference(Action<Driver> scenario)
    {
        Driver driver = new(Anchor, Anchor, 0);
        scenario(driver);
        Assert.That(driver.Comparisons, Is.GreaterThan(0));
    }

    private static ulong[] Balances(int count, ulong each = Gwei32Eth) => Enumerable.Repeat(each, count).ToArray();

    private static void ParentCollectsTwoCommittees(Driver d)
    {
        d.Rebase(new CheckpointRef(3, GetRoot(96)), new CheckpointRef(2, GetRoot(96)), 96);
        ulong[] balances = Balances(256);
        void Committee(int k, ulong root, ulong epoch)
        {
            for (ulong v = (ulong)k * 8; v < (ulong)(k + 1) * 8; v++) d.Vote(v, root, epoch);
        }

        d.Block(127, 127, 96);
        for (int k = 0; k < 32; k++) Committee(k, 127, 3);
        d.Head(balances, 127);
        d.Block(128, 128, 127);
        Committee(31, 127, 3);
        d.Head(balances, 128);
        d.Block(129, 129, 128);
        d.Head(balances, 129);
        d.Block(130, 130, 129);
        Committee(1, 129, 4);
        d.Head(balances, 130, boost: 130);
        d.Head(balances, 131);
        d.Block(131, 131, 130);
        Committee(2, 130, 4);
        d.Head(balances, 131);
        Committee(3, 130, 4);
        d.Head(balances, 132);
    }

    private static void VotesAndBalancesMove(Driver d)
    {
        d.Block(1, 1, 0);
        d.Block(1, 2, 0);
        d.Block(2, 3, 1);
        d.Vote(0, 1, 1);
        d.Vote(1, 3, 1);
        d.Head([Gwei32Eth, Gwei32Eth], 2);
        d.Vote(0, 2, 2);
        d.Head([30_000_000_000, Gwei32Eth], 2);
        d.Head([0, Gwei32Eth], 2);
        d.Vote(2, 3, 2);
        d.Head([Gwei32Eth, 31_000_000_000, Gwei32Eth], 3);
        d.Head([Gwei32Eth, 31_000_000_000], 3);
    }

    private static void BoostMovesAcrossBranchesAndInvalidation(Driver d)
    {
        ulong[] balances = Balances(64);
        d.Block(1, 1, 0);
        d.Block(2, 2, 1);
        d.Block(2, 3, 1);
        d.Block(3, 4, 3);
        for (ulong v = 0; v < 20; v++) d.Vote(v, 2, 0);
        for (ulong v = 20; v < 30; v++) d.Vote(v, 4, 0);
        d.Head(balances, 2, boost: 2);
        d.Head(balances, 3, boost: 4);
        d.Head(balances, 3, boost: 4);
        d.Invalidate(3);
        d.Head(balances, 3, boost: 4);
        d.Head(balances, 4, boost: 2);
        d.Block(4, 5, 2);
        d.Head(balances, 4, boost: 5);
        d.Head(balances, 5);
    }

    private static void PruneBetweenUpdates(Driver d)
    {
        ulong[] balances = Balances(16);
        for (ulong i = 1; i <= 6; i++) d.Block(i, i, i - 1);
        d.Block(4, 7, 3);
        for (ulong v = 0; v < 8; v++) d.Vote(v, 6, 0);
        for (ulong v = 8; v < 16; v++) d.Vote(v, 7, 0);
        d.Head(balances, 6, boost: 6);
        d.Prune(3);
        d.Head(balances, 7, boost: 6);
        d.Vote(0, 7, 1);
        d.Head(balances, 7);
    }

    private static void EquivocationUnderBoost(Driver d)
    {
        ulong[] balances = Balances(8);
        d.Block(1, 1, 0);
        d.Block(1, 2, 0);
        for (ulong v = 0; v < 4; v++) d.Vote(v, 1, 0);
        for (ulong v = 4; v < 8; v++) d.Vote(v, 2, 0);
        d.Head(balances, 1, boost: 1);
        d.Slash(0, 5);
        d.Head(balances, 2, boost: 1);
        d.Vote(1, 2, 1);
        d.Head(balances, 2);
    }

    private static void BoostUnderAnInvalidAncestor(Driver d)
    {
        // Invalidating 3 back to block 0 throws at the valid block 1 after invalidating 3 and 2, leaving 4 optimistic under 2.
        ulong[] balances = Balances(8);
        d.Block(1, 1, 0, ExecutionStatus.Valid);
        d.Block(2, 2, 1);
        d.Block(3, 3, 2);
        d.Block(3, 4, 2);
        for (ulong v = 0; v < 8; v++) d.Vote(v, 4, 0);
        d.Head(balances, 3, boost: 4);
        d.PartiallyInvalidate(3, 0);
        d.Head(balances, 4, boost: 4);
        d.Head(balances, 4);
    }

    public sealed class Driver
    {
        private readonly List<ulong> _weights = [];
        private readonly List<(Hash256 Current, Hash256 Next, ulong Epoch)> _votes = [];
        private readonly HashSet<ulong> _equivocating = [];
        private ProtoArrayForkChoice _forkChoice = null!;
        private CheckpointRef _justified;
        private CheckpointRef _finalized;
        private ulong[] _oldBalances = [];
        private Hash256 _previousBoostRoot = Hash256.Zero;
        private ulong _previousBoostScore;

        public Driver(CheckpointRef justified, CheckpointRef finalized, ulong anchorSlot) => Rebase(justified, finalized, anchorSlot);

        public int Comparisons { get; private set; }

        public void Rebase(CheckpointRef justified, CheckpointRef finalized, ulong anchorSlot)
        {
            _justified = justified;
            _finalized = finalized;
            _forkChoice = new ProtoArrayForkChoice(anchorSlot, anchorSlot, Hash256.Zero, justified, finalized, ExecutionStatus.Optimistic, finalized.Root, SlotsPerEpoch);
            _weights.Clear();
            _weights.Add(0);
        }

        public void Block(ulong slot, ulong root, ulong parent, ExecutionStatus status = ExecutionStatus.Optimistic)
        {
            _forkChoice.ProcessBlock(
                new ProtoBlock(slot, GetRoot(root), GetRoot(parent), Hash256.Zero, _justified, _finalized, status, GetRoot(root), null, null),
                slot, _justified, _finalized);
            _weights.Add(0);
        }

        public void Vote(ulong validator, ulong root, ulong epoch)
        {
            _forkChoice.ProcessAttestation(validator, GetRoot(root), epoch);
            while (_votes.Count <= (int)validator) _votes.Add((Hash256.Zero, Hash256.Zero, 0));
            (Hash256 current, Hash256 next, ulong nextEpoch) = _votes[(int)validator];
            bool unset = current == Hash256.Zero && next == Hash256.Zero && nextEpoch == 0;
            if (epoch > nextEpoch || unset) _votes[(int)validator] = (current, GetRoot(root), epoch);
        }

        public void Slash(params ulong[] validators)
        {
            _forkChoice.OnAttesterSlashing(validators);
            _equivocating.UnionWith(validators);
        }

        public void Invalidate(ulong root) =>
            _forkChoice.ProcessExecutionPayloadInvalidation(InvalidationOperation.InvalidateOne(GetRoot(root)), _finalized);

        public void PartiallyInvalidate(ulong root, ulong latestValidRoot) =>
            Assert.That(
                () => _forkChoice.ProcessExecutionPayloadInvalidation(InvalidationOperation.InvalidateMany(GetRoot(root), true, GetRoot(latestValidRoot)), _finalized),
                Throws.InstanceOf<ProtoArrayException>());

        public void Prune(ulong root)
        {
            int before = _forkChoice.Count;
            _forkChoice.PruneThreshold = 0;
            _forkChoice.MaybePrune(GetRoot(root));
            _weights.RemoveRange(0, before - _forkChoice.Count);
            _justified = _finalized = new CheckpointRef(0, GetRoot(root));
        }

        public void Head(ulong[] balances, ulong slot, ulong? boost = null)
        {
            Hash256 boostRoot = boost is ulong b ? GetRoot(b) : Hash256.Zero;
            JustifiedBalances justifiedBalances = JustifiedBalances.FromEffectiveBalances(balances);
            _forkChoice.SetProposerBoostRoot(boostRoot);
            _forkChoice.GetHead(_justified, _finalized, justifiedBalances, null, slot);
            ApplySinglePass(balances, justifiedBalances, boostRoot);

            IReadOnlyList<ProtoNode> nodes = _forkChoice.Nodes;
            using (Assert.EnterMultipleScope())
            {
                for (int i = 0; i < nodes.Count; i++)
                {
                    Assert.That(nodes[i].Weight, Is.EqualTo(_weights[i]), $"node {nodes[i].Root} after an update at slot {slot}");
                }
            }

            Comparisons++;
        }

        private void ApplySinglePass(ulong[] newBalances, JustifiedBalances justifiedBalances, Hash256 proposerBoostRoot)
        {
            IReadOnlyList<ProtoNode> nodes = _forkChoice.Nodes;
            Dictionary<Hash256, int> indices = [];
            for (int i = 0; i < nodes.Count; i++) indices[nodes[i].Root] = i;

            long[] deltas = new long[nodes.Count];
            for (int v = 0; v < _votes.Count; v++)
            {
                (Hash256 current, Hash256 next, ulong epoch) = _votes[v];
                if (current == Hash256.Zero && next == Hash256.Zero) continue;
                ulong oldBalance = v < _oldBalances.Length ? _oldBalances[v] : 0;
                if (_equivocating.Contains((ulong)v))
                {
                    if (current != Hash256.Zero)
                    {
                        if (indices.TryGetValue(current, out int ci)) deltas[ci] -= (long)oldBalance;
                        _votes[v] = (Hash256.Zero, next, epoch);
                    }

                    continue;
                }

                ulong newBalance = v < newBalances.Length ? newBalances[v] : 0;
                if (current != next || oldBalance != newBalance)
                {
                    if (indices.TryGetValue(current, out int ci)) deltas[ci] -= (long)oldBalance;
                    if (indices.TryGetValue(next, out int ni)) deltas[ni] += (long)newBalance;
                    _votes[v] = (next, next, epoch);
                }
            }

            _oldBalances = newBalances;

            ulong proposerScore = 0;
            for (int nodeIndex = nodes.Count - 1; nodeIndex >= 0; nodeIndex--)
            {
                ProtoNode node = nodes[nodeIndex];
                if (node.Root == Hash256.Zero) continue;

                bool invalid = node.ExecutionStatus == ExecutionStatus.Invalid;
                long nodeDelta = invalid ? -(long)_weights[nodeIndex] : deltas[nodeIndex];
                if (_previousBoostRoot != Hash256.Zero && _previousBoostRoot == node.Root && !invalid)
                {
                    nodeDelta -= (long)_previousBoostScore;
                }

                if (proposerBoostRoot != Hash256.Zero && proposerBoostRoot == node.Root && !invalid)
                {
                    proposerScore = _forkChoice.CalculateCommitteeFraction(justifiedBalances, ProtoArrayForkChoice.DefaultProposerScoreBoostPercent);
                    nodeDelta += (long)proposerScore;
                }

                _weights[nodeIndex] = invalid ? 0 : checked((ulong)((long)_weights[nodeIndex] + nodeDelta));
                if (node.Parent is int parentIndex) deltas[parentIndex] += nodeDelta;
            }

            _previousBoostRoot = proposerBoostRoot;
            _previousBoostScore = proposerScore;
        }
    }
}
