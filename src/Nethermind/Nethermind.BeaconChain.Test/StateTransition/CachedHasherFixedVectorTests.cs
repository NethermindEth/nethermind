// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition.Hashing;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using NUnit.Framework;
using static Nethermind.BeaconChain.Test.StateTransition.GloasTestFixtures;

namespace Nethermind.BeaconChain.Test.StateTransition;

/// <summary>
/// A state whose fixed-length vector has the wrong in-memory length is not decodable, so the cached
/// hasher must refuse it like the generated one instead of hashing a truncated or padded vector into a
/// root that no other client can reproduce.
/// </summary>
[HardTimeout(60_000)]
public class CachedHasherFixedVectorTests
{
    private const int ValidatorCount = 64;

    public static IEnumerable<TestCaseData> FuluCases() => Cases(FuluFields);

    public static IEnumerable<TestCaseData> GloasCases() => Cases(GloasFields);

    [TestCaseSource(nameof(FuluCases))]
    public void Fulu_cached_hasher_matches_generated_hasher_for_vector_length(string field, int? length, bool warm)
    {
        FixedField<BeaconStateFulu> target = FuluFields.Find(f => f.Name == field)!;
        CachedBeaconStateHasher hasher = new();
        BeaconStateFulu state = CreateFuluStateAtBoundary(ValidatorCount);
        if (warm)
            hasher.HashTreeRoot(state);

        target.Resize(state, length);

        AssertParity(() => SszRoots.HashTreeRoot(state), () => hasher.HashTreeRoot(state), field, length);
        target.Resize(state, target.ExactLength);
        Assert.That(hasher.HashTreeRoot(state), Is.EqualTo(SszRoots.HashTreeRoot(state)), "a refused state must not poison the caches");
    }

    [TestCaseSource(nameof(GloasCases))]
    public void Gloas_cached_hasher_matches_generated_hasher_for_vector_length(string field, int? length, bool warm)
    {
        FixedField<BeaconStateGloas> target = GloasFields.Find(f => f.Name == field)!;
        CachedBeaconStateHasher hasher = new();
        BeaconStateGloas state = CreateGloasState(out _, out _);
        if (warm)
            hasher.HashTreeRoot(state);

        target.Resize(state, length);

        AssertParity(() => SszRoots.HashTreeRoot(state), () => hasher.HashTreeRoot(state), field, length);
        target.Resize(state, target.ExactLength);
        Assert.That(hasher.HashTreeRoot(state), Is.EqualTo(SszRoots.HashTreeRoot(state)), "a refused state must not poison the caches");
    }

    [Test]
    public void First_Gloas_state_releases_the_Fulu_only_caches_and_a_Fulu_sibling_still_hashes_correctly()
    {
        CachedBeaconStateHasher hasher = new();
        BeaconStateFulu fulu = CreateFuluStateAtBoundary(ValidatorCount);
        hasher.HashTreeRoot(fulu);
        Assert.That(SnapshotLength(hasher, "_validators"), Is.GreaterThan(0), "fixture: the Fulu hash fills the validator cache");
        Assert.That(SnapshotLength(hasher, "_balances"), Is.GreaterThan(0), "fixture: the Fulu hash fills the balance cache");

        BeaconStateGloas gloas = CreateGloasState(out _, out _);
        Assert.That(hasher.HashTreeRoot(gloas), Is.EqualTo(SszRoots.HashTreeRoot(gloas)));

        Assert.That(SnapshotLength(hasher, "_validators"), Is.Zero, "validators");
        Assert.That(SnapshotLength(hasher, "_balances"), Is.Zero, "balances");
        Assert.That(SnapshotLength(hasher, "_inactivityScores"), Is.Zero, "inactivity scores");
        Assert.That(SnapshotLength(hasher, "_previousEpochParticipation"), Is.Zero, "previous participation");
        Assert.That(SnapshotLength(hasher, "_currentEpochParticipation"), Is.Zero, "current participation");
        Assert.That(hasher.HashTreeRoot(fulu), Is.EqualTo(SszRoots.HashTreeRoot(fulu)), "a Fulu sibling after the Gloas state");
    }

    [Test]
    public void Refused_Gloas_state_keeps_the_Fulu_caches()
    {
        CachedBeaconStateHasher hasher = new();
        BeaconStateFulu fulu = CreateFuluStateAtBoundary(ValidatorCount);
        hasher.HashTreeRoot(fulu);
        BeaconStateGloas gloas = CreateGloasState(out _, out _);
        gloas.JustificationBits = Bits(3);

        Assert.Throws<InvalidDataException>(() => hasher.HashTreeRoot(gloas));

        Assert.That(SnapshotLength(hasher, "_validators"), Is.GreaterThan(0), "a refused state must not touch any cache");
        Assert.That(SnapshotLength(hasher, "_balances"), Is.GreaterThan(0));
    }

    [Test]
    public void Fulu_cached_hasher_refuses_a_null_root_element_like_the_generated_hasher(
        [Values("BlockRoots", "StateRoots", "RandaoMixes")] string field, [Values] bool warm)
    {
        CachedBeaconStateHasher hasher = new();
        BeaconStateFulu state = CreateFuluStateAtBoundary(ValidatorCount);
        AssertNullRootElement(state, field, warm, RootVector(state, field), s => hasher.HashTreeRoot(s), s => SszRoots.HashTreeRoot(s));
    }

    [Test]
    public void Gloas_cached_hasher_refuses_a_null_root_element_like_the_generated_hasher(
        [Values("BlockRoots", "StateRoots", "RandaoMixes")] string field, [Values] bool warm)
    {
        CachedBeaconStateHasher hasher = new();
        BeaconStateGloas state = CreateGloasState(out _, out _);
        AssertNullRootElement(state, field, warm, RootVector(state, field), s => hasher.HashTreeRoot(s), s => SszRoots.HashTreeRoot(s));
    }

    private static Hash256[] RootVector(BeaconStateFulu state, string field) => field switch
    {
        "BlockRoots" => state.BlockRoots!,
        "StateRoots" => state.StateRoots!,
        _ => state.RandaoMixes!,
    };

    private static Hash256[] RootVector(BeaconStateGloas state, string field) => field switch
    {
        "BlockRoots" => state.BlockRoots!,
        "StateRoots" => state.StateRoots!,
        _ => state.RandaoMixes!,
    };

    // An earlier changed element plus a later null proves a refusal cannot leave a half-updated tree behind.
    private static void AssertNullRootElement<TState>(TState state, string field, bool warm, Hash256[] vector,
        Func<TState, Hash256> cached, Func<TState, Hash256> generated)
    {
        if (warm)
            cached(state);

        vector[2] = new Hash256(new byte[32].Select((_, i) => (byte)(i + 1)).ToArray());
        vector[5] = null!;

        Assert.Throws<NullReferenceException>(() => generated(state), "fixture: the generated hasher refuses a null element");
        AssertParity(() => generated(state), () => cached(state), field, null);

        vector[5] = new Hash256(new byte[32].Select((_, i) => (byte)(i + 9)).ToArray());
        Assert.That(cached(state), Is.EqualTo(generated(state)), "a refused state must not poison the caches");
    }

    private static int SnapshotLength(CachedBeaconStateHasher hasher, string cacheField)
    {
        object cache = typeof(CachedBeaconStateHasher).GetField(cacheField, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(hasher)!;
        return ((Array)cache.GetType().GetField("_snapshot", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(cache)!).Length;
    }

    private static void AssertParity(Func<Hash256> generated, Func<Hash256> cached, string field, int? length)
    {
        string label = $"{field} length {(length is null ? "null" : length.ToString())}";
        Hash256? expected = null;
        Exception? expectedFailure = null;
        try { expected = generated(); }
        catch (Exception e) { expectedFailure = e; }

        Hash256? actual = null;
        Exception? actualFailure = null;
        try { actual = cached(); }
        catch (Exception e) { actualFailure = e; }

        if (expectedFailure is not null)
        {
            Assert.That(actualFailure, Is.Not.Null.And.TypeOf(expectedFailure.GetType()), $"{label}: the generated hasher throws {expectedFailure.GetType().Name} but the cached one returned {actual}");
        }
        else
        {
            Assert.That(actualFailure, Is.Null, $"{label}: the generated hasher accepts it but the cached one threw {actualFailure}");
            Assert.That(actual, Is.EqualTo(expected), label);
        }
    }

    private static IEnumerable<TestCaseData> Cases<TState>(List<FixedField<TState>> fields)
    {
        foreach (FixedField<TState> field in fields)
        {
            foreach (int? length in (int?[])[null, 0, 1, field.ExactLength - 1, field.ExactLength, field.ExactLength + 1, field.ExactLength * 2])
            {
                foreach (bool warm in (bool[])[false, true])
                {
                    yield return new TestCaseData(field.Name, length, warm).SetName($"{{m}}({field.Name}, {(length is null ? "null" : length)}, warm: {warm})");
                }
            }
        }
    }

    private sealed record FixedField<TState>(string Name, int ExactLength, Action<TState, int?> Resize);

    private static readonly List<FixedField<BeaconStateFulu>> FuluFields =
    [
        new("BlockRoots", (int)Presets.SlotsPerHistoricalRoot, (s, n) => s.BlockRoots = Roots(n)),
        new("StateRoots", (int)Presets.SlotsPerHistoricalRoot, (s, n) => s.StateRoots = Roots(n)),
        new("RandaoMixes", (int)Presets.EpochsPerHistoricalVector, (s, n) => s.RandaoMixes = Roots(n)),
        new("Slashings", (int)Presets.EpochsPerSlashingsVector, (s, n) => s.Slashings = Words(n)),
        new("JustificationBits", 4, (s, n) => s.JustificationBits = Bits(n)),
        new("ProposerLookahead", (int)Presets.ProposerLookaheadSlots, (s, n) => s.ProposerLookahead = Words(n)),
    ];

    private static readonly List<FixedField<BeaconStateGloas>> GloasFields =
    [
        new("BlockRoots", (int)Presets.SlotsPerHistoricalRoot, (s, n) => s.BlockRoots = Roots(n)),
        new("StateRoots", (int)Presets.SlotsPerHistoricalRoot, (s, n) => s.StateRoots = Roots(n)),
        new("RandaoMixes", (int)Presets.EpochsPerHistoricalVector, (s, n) => s.RandaoMixes = Roots(n)),
        new("Slashings", (int)Presets.EpochsPerSlashingsVector, (s, n) => s.Slashings = Words(n)),
        new("JustificationBits", 4, (s, n) => s.JustificationBits = Bits(n)),
        new("ProposerLookahead", (int)Presets.ProposerLookaheadSlots, (s, n) => s.ProposerLookahead = Words(n)),
        new("ExecutionPayloadAvailability", (int)Presets.SlotsPerHistoricalRoot, (s, n) => s.ExecutionPayloadAvailability = Bits(n)),
        new("BuilderPendingPayments", (int)Presets.BuilderPendingPaymentsLength, (s, n) => s.BuilderPendingPayments = Items<BuilderPendingPayment>(n)),
        new("PtcWindow", (int)Presets.PtcWindowLength, (s, n) => s.PtcWindow = Items<PayloadTimelinessCommittee>(n)),
    ];

    private static Hash256[]? Roots(int? length)
    {
        if (length is null)
            return null;
        Hash256[] roots = new Hash256[length.Value];
        Array.Fill(roots, Hash256.Zero);
        return roots;
    }

    private static ulong[]? Words(int? length)
    {
        if (length is null)
            return null;
        ulong[] words = new ulong[length.Value];
        Array.Fill(words, 7UL);
        return words;
    }

    private static BitArray? Bits(int? length) => length is null ? null : new BitArray(length.Value, true);

    private static T[]? Items<T>(int? length) where T : new()
    {
        if (length is null)
            return null;
        T[] items = new T[length.Value];
        for (int i = 0; i < items.Length; i++)
        {
            items[i] = new T();
        }
        return items;
    }
}
