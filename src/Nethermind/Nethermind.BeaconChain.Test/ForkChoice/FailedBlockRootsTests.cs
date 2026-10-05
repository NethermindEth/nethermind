// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.ForkChoice;
using Nethermind.Core.Crypto;

namespace Nethermind.BeaconChain.Test.ForkChoice;

public class FailedBlockRootsTests
{
    private static Hash256 Root(int i)
    {
        byte[] bytes = new byte[32];
        BitConverter.TryWriteBytes(bytes, i);
        return new Hash256(bytes);
    }

    [Test]
    public void A_full_set_drops_its_oldest_root_and_never_grows_past_its_capacity()
    {
        FailedBlockRoots roots = new();
        for (int i = 0; i < FailedBlockRoots.Capacity + 10; i++)
        {
            roots.Add(Root(i), (ulong)i);
        }

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(roots.Count, Is.EqualTo(FailedBlockRoots.Capacity));
        Assert.That(roots.Contains(Root(9)), Is.False, "the oldest roots made room");
        Assert.That(roots.Contains(Root(10)), Is.True);
        Assert.That(roots.Contains(Root(FailedBlockRoots.Capacity + 9)), Is.True);
    }

    [Test]
    public void Adding_a_recorded_root_again_evicts_nothing()
    {
        FailedBlockRoots roots = new();
        for (int i = 0; i < FailedBlockRoots.Capacity; i++)
        {
            roots.Add(Root(i), (ulong)i);
        }

        roots.Add(Root(FailedBlockRoots.Capacity - 1), 0);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(roots.Count, Is.EqualTo(FailedBlockRoots.Capacity));
        Assert.That(roots.Contains(Root(0)), Is.True);
    }

    [Test]
    public void Prune_drops_the_roots_at_or_below_the_slot_and_keeps_the_rest()
    {
        FailedBlockRoots roots = new();
        for (int i = 0; i < 6; i++)
        {
            roots.Add(Root(i), (ulong)i);
        }

        roots.Prune(3);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(roots.Count, Is.EqualTo(2));
        Assert.That(new[] { 0, 1, 2, 3 }, Has.None.Matches<int>(i => roots.Contains(Root(i))));
        Assert.That(new[] { 4, 5 }, Has.All.Matches<int>(i => roots.Contains(Root(i))));
    }

    [Test]
    public void Concurrent_writers_readers_and_pruning_keep_the_set_bounded()
    {
        FailedBlockRoots roots = new();

        Assert.DoesNotThrow(() => Parallel.For(0, 8, worker =>
        {
            for (int i = 0; i < 4 * FailedBlockRoots.Capacity; i++)
            {
                Hash256 root = Root(worker * 1_000_000 + i);
                roots.Add(root, (ulong)i);
                roots.Contains(root);
                if (i % 100 == 0)
                {
                    roots.Prune((ulong)(i / 2));
                }
            }
        }));

        Assert.That(roots.Count, Is.LessThanOrEqualTo(FailedBlockRoots.Capacity));
    }
}
