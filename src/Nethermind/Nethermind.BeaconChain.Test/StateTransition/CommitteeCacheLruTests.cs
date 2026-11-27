// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.StateTransition.Shuffling;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;

namespace Nethermind.BeaconChain.Test.StateTransition;

[HardTimeout(60_000)]
public class CommitteeCacheLruTests
{
    [Test]
    public async Task Concurrent_requests_reuse_the_same_assignment()
    {
        BeaconStateFulu state = CreateState(1);
        CommitteeCacheLru cache = new();
        int[] expected = CommitteeCache.Build(state, 2).ShuffledIndices.ToArray();

        CommitteeCache[] results = await Task.WhenAll(Enumerable.Range(0, 32)
            .Select(_ => Task.Run(() => cache.GetOrBuild(state, 2))));

        using (Assert.EnterMultipleScope())
        {
            foreach (CommitteeCache result in results)
            {
                Assert.That(result, Is.SameAs(results[0]));
                Assert.That(result.ShuffledIndices.ToArray(), Is.EqualTo(expected));
            }
        }
    }

    [Test]
    public async Task Concurrent_evictions_keep_assignments_separate()
    {
        BeaconStateFulu[] states = [.. Enumerable.Range(1, 8).Select(i => CreateState((byte)i))];
        int[][] expected = [.. states.Select(state => CommitteeCache.Build(state, 2).ShuffledIndices.ToArray())];
        CommitteeCacheLru cache = new();

        await Task.WhenAll(Enumerable.Range(0, 16).Select(worker => Task.Run(() =>
        {
            for (int i = 0; i < states.Length; i++)
            {
                int index = (worker + i) % states.Length;
                Assert.That(cache.GetOrBuild(states[index], 2).ShuffledIndices.ToArray(), Is.EqualTo(expected[index]));
            }
        })));
    }

    [Test]
    public void Least_recently_used_assignment_is_evicted()
    {
        BeaconStateFulu first = CreateState(1);
        BeaconStateFulu second = CreateState(2);
        CommitteeCacheLru cache = new(capacity: 2);
        CommitteeCache retained = cache.GetOrBuild(first, 2);
        CommitteeCache evicted = cache.GetOrBuild(second, 2);
        Assert.That(cache.GetOrBuild(first, 2), Is.SameAs(retained));

        cache.GetOrBuild(CreateState(3), 2);

        Assert.That(cache.GetOrBuild(first, 2), Is.SameAs(retained));
        CommitteeCache rebuilt = cache.GetOrBuild(second, 2);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(rebuilt, Is.Not.SameAs(evicted));
            Assert.That(rebuilt.ShuffledIndices.ToArray(), Is.EqualTo(evicted.ShuffledIndices.ToArray()));
        }
    }

    [Test]
    public void Failed_build_can_be_retried()
    {
        BeaconStateFulu state = CreateState(1);
        Validator[] validators = state.Validators!;
        state.Validators = [];
        CommitteeCacheLru cache = new();
        Assert.That(() => cache.GetOrBuild(state, 2), Throws.TypeOf<BeaconStateException>());

        state.Validators = validators;

        Assert.That(cache.GetOrBuild(state, 2).ShuffledIndices.ToArray(),
            Is.EqualTo(CommitteeCache.Build(state, 2).ShuffledIndices.ToArray()));
    }

    [Test]
    public void Electra_state_does_not_require_a_fulu_layout()
    {
        BeaconStateFulu fulu = CreateState(1);
        BeaconStateElectra electra = new()
        {
            Slot = fulu.Slot,
            BlockRoots = fulu.BlockRoots,
            RandaoMixes = fulu.RandaoMixes,
            Validators = fulu.Validators,
        };
        CommitteeCacheLru cache = new();
        CommitteeCache assignment = cache.GetOrBuild(electra, 2);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(assignment.ShuffledIndices.ToArray(), Is.EqualTo(CommitteeCache.Build(electra, 2).ShuffledIndices.ToArray()));
            Assert.That(cache.GetOrBuild(fulu, 2), Is.SameAs(assignment));
        }
    }

    private static BeaconStateFulu CreateState(byte branch)
    {
        BeaconStateFulu state = GloasTestFixtures.CreateFuluState(64);
        state.Slot = 2 * Presets.SlotsPerEpoch;
        byte[] root = new byte[32];
        root[0] = branch;
        state.BlockRoots![(int)Presets.SlotsPerEpoch - 1] = new Hash256(root);
        state.RandaoMixes![0] = new Hash256(root);
        return state;
    }
}
