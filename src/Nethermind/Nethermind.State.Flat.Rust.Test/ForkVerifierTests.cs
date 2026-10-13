// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.State.Flat.Persistence;
using NUnit.Framework;

namespace Nethermind.State.Flat.Rust.Test;

/// <summary>
/// The forked trie verifier, the original's verification over the forked bundles, against the original on a node of
/// each backend holding the same state: the same verdict and the same counts on a good database and on one with an
/// account the trie does not hold.
/// </summary>
[Parallelizable(ParallelScope.All)]
public class ForkVerifierTests
{
    private static readonly Address A = new("0x2222222200000000000000000000000000000001");
    private static readonly Address B = new("0x2222222200000000000000000000000000000002");
    private static readonly Address Stray = new("0x2222222200000000000000000000000000000003");

    [Test]
    public async Task Fork_verifier_reports_what_the_original_reports([Values] bool corrupted)
    {
        RustNativeLibrary.Require();
        (bool Ok, string Stats) original = await Verify(FlatDbBackend.CSharp, corrupted);
        (bool Ok, string Stats) fork = await Verify(FlatDbBackend.Rust, corrupted);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(fork, Is.EqualTo(original));
            Assert.That(original.Ok, Is.Not.EqualTo(corrupted));
        }
    }

    private static async Task<(bool Ok, string Stats)> Verify(FlatDbBackend backend, bool corrupted)
    {
        await using WorldStateNode node = new(backend);
        IWorldStateManager manager = node.WorldStateManager;
        BlockHeader first = WorldStateNode.Commit(manager.GlobalWorldState, null, 0, WorldStateNode.Write(A, new Account(1, 10), false, (1, 11), (2, 12)));
        BlockHeader second = WorldStateNode.Commit(manager.GlobalWorldState, first, 1, WorldStateNode.Write(B, new Account(2, 20), false, (UInt256.MaxValue, 1)));
        manager.FlushCache(CancellationToken.None);
        if (corrupted)
        {
            StateId persisted = new(second);
            using IPersistence.IWriteBatch batch = node.Resolve<IPersistence>().CreateWriteBatch(persisted, persisted);
            batch.SetAccount(Stray, new Account(9, 99));
        }

        bool ok = manager.VerifyTrie(second, CancellationToken.None);
        string stats = backend == FlatDbBackend.Rust
            ? node.Resolve<Fork.FlatTrieVerifier>().Stats.ToString()
            : node.Resolve<FlatTrieVerifier>().Stats.ToString();
        return (ok, stats);
    }
}
