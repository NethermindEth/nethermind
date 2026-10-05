// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Reflection;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.StateTransition.Hashing;
using Nethermind.BeaconChain.Sync;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using NUnit.Framework;

namespace Nethermind.BeaconChain.Test.Sync;

public class GloasLineageHasherTests
{
    private const ulong ForkSlot = 32;

    [Test]
    public void Lineage_keeps_a_cached_hasher_and_a_fork_branch_adopts_a_fresh_one()
    {
        SignedGloasChain chain = new(new FullBeaconStateHasher());
        BlockImporter importer = chain.CreateImporter(new SignedGloasChain.EnvelopeEngine());
        SignedGloasChain.Block first = chain.Next(null, ForkSlot, full: false, 0xA1);
        SignedGloasChain.Block second = chain.Next(first, ForkSlot + 1, full: false, 0xA2);
        SignedGloasChain.Block fork = chain.Next(first, ForkSlot + 2, full: false, 0xA3);
        SignedGloasChain.Block forkChild = chain.Next(fork, ForkSlot + 3, full: false, 0xA4);
        IBeaconStateHasher initial = LineageHasher(importer);

        Assert.That(importer.Import(first.Forked, first.Root, verifySignatures: true), Is.EqualTo(BlockImportResult.Imported));
        IBeaconStateHasher afterFirst = LineageHasher(importer);
        Assert.That(importer.Import(second.Forked, second.Root, verifySignatures: true), Is.EqualTo(BlockImportResult.Imported));
        IBeaconStateHasher afterSecond = LineageHasher(importer);
        Assert.That(importer.Import(fork.Forked, fork.Root, verifySignatures: true), Is.EqualTo(BlockImportResult.Imported));
        IBeaconStateHasher afterFork = LineageHasher(importer);
        Assert.That(importer.Import(forkChild.Forked, forkChild.Root, verifySignatures: true), Is.EqualTo(BlockImportResult.Imported));
        IBeaconStateHasher afterForkChild = LineageHasher(importer);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(new[] { initial, afterFirst, afterSecond, afterFork, afterForkChild }, Has.All.TypeOf<CachedBeaconStateHasher>());
        Assert.That(afterSecond, Is.SameAs(afterFirst), "a child of the lineage block reuses the lineage hasher");
        Assert.That(afterFork, Is.Not.SameAs(afterSecond), "a fork branch does not inherit the previous branch's memo");
        Assert.That(afterForkChild, Is.SameAs(afterFork));
    }

    /// <summary>
    /// The importer refuses a block whose state root, sealed by the full hasher, differs from the lineage hasher's, so every
    /// import here proves the two agree; the direct comparison also covers hashing across branches.
    /// </summary>
    [Test]
    public void Cached_roots_equal_the_full_hasher_across_a_forked_chain()
    {
        SignedGloasChain chain = new(new FullBeaconStateHasher());
        SignedGloasChain.Block first = chain.Next(null, ForkSlot, full: false, 0xA1);
        SignedGloasChain.Block second = chain.Next(first, ForkSlot + 1, full: true, 0xA2);
        SignedGloasChain.Block fork = chain.Next(first, ForkSlot + 2, full: false, 0xA3);
        SignedGloasChain.Block third = chain.Next(second, ForkSlot + 3, full: true, 0xA4);
        CachedBeaconStateHasher cached = new();

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        foreach (SignedGloasChain.Block block in (SignedGloasChain.Block[])[first, second, fork, third, second])
        {
            Assert.That(cached.HashTreeRoot(block.PostState), Is.EqualTo(SszRoots.HashTreeRoot(block.PostState)), $"state at slot {block.PostState.Slot}");
        }
    }

    [Test]
    public void Envelope_of_the_lineage_block_is_verified_through_the_lineage_hasher()
    {
        SignedGloasChain chain = new(new FullBeaconStateHasher());
        BlockImporter importer = chain.CreateImporter(new SignedGloasChain.EnvelopeEngine());
        SignedGloasChain.Block first = chain.Next(null, ForkSlot, full: false, 0xA1);
        Assert.That(importer.Import(first.Forked, first.Root, verifySignatures: true), Is.EqualTo(BlockImportResult.Imported));
        CountingStateHasher spy = new(LineageHasher(importer));
        LineageCache(importer).Hasher = spy;

        ExecutionPayloadEnvelopeImportResult result = importer.ImportEnvelope(first.Envelope);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(result, Is.EqualTo(ExecutionPayloadEnvelopeImportResult.Valid));
        Assert.That(spy.GloasCalls, Is.EqualTo(1));
    }

    [Test]
    public void Report_the_cost_of_rehashing_a_post_state_with_each_hasher()
    {
        const int Rounds = 200;
        SignedGloasChain chain = new();
        BeaconStateGloas state = chain.Next(null, ForkSlot, full: false, 0xA1).PostState;
        CachedBeaconStateHasher cached = new();
        Hash256 warm = cached.HashTreeRoot(state);

        long fullStart = Stopwatch.GetTimestamp();
        for (int i = 0; i < Rounds; i++)
        {
            SszRoots.HashTreeRoot(state);
        }

        TimeSpan full = Stopwatch.GetElapsedTime(fullStart);
        long cachedStart = Stopwatch.GetTimestamp();
        for (int i = 0; i < Rounds; i++)
        {
            cached.HashTreeRoot(state);
        }

        TimeSpan warmCached = Stopwatch.GetElapsedTime(cachedStart);
        TestContext.Out.WriteLine($"Gloas state root, {state.Validators!.Length} validators, {Rounds} rounds: full {full.TotalMilliseconds / Rounds:F3} ms each, cached {warmCached.TotalMilliseconds / Rounds:F3} ms each");
        Assert.That(warm, Is.EqualTo(SszRoots.HashTreeRoot(state)));
    }

    private static EpochCache LineageCache(BlockImporter importer) =>
        (EpochCache)typeof(BlockImporter).GetField("_gloasLineageCache", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(importer)!;

    private static IBeaconStateHasher LineageHasher(BlockImporter importer) => LineageCache(importer).Hasher;

    internal sealed class CountingStateHasher(IBeaconStateHasher inner) : IBeaconStateHasher
    {
        public int GloasCalls { get; private set; }

        public Hash256 HashTreeRoot(BeaconStateFulu state) => inner.HashTreeRoot(state);

        public Hash256 HashTreeRoot(BeaconStateGloas state)
        {
            GloasCalls++;
            return inner.HashTreeRoot(state);
        }
    }
}
