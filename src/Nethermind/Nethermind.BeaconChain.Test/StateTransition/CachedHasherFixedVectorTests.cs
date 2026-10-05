// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections;
using System.IO;
using System.Reflection;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition.Hashing;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using static Nethermind.BeaconChain.Test.StateTransition.GloasTestFixtures;

namespace Nethermind.BeaconChain.Test.StateTransition;

[HardTimeout(60_000)]
public class CachedHasherFixedVectorTests
{
    private const int ValidatorCount = 64;

    public static IEnumerable<TestCaseData> FuluCases() => Cases(FuluFields, false, "Fulu_cached_hasher_matches_generated_hasher_for_vector_length");
    public static IEnumerable<TestCaseData> GloasCases() => Cases(GloasFields, true, "Gloas_cached_hasher_matches_generated_hasher_for_vector_length");

    [TestCaseSource(nameof(FuluCases))]
    [TestCaseSource(nameof(GloasCases))]
    public void Cached_hasher_matches_generated_hasher_for_vector_length(bool gloas, string field, int? length, bool warm)
    {
        FixedField target = (gloas ? GloasFields : FuluFields).Find(f => f.Name == field)!;
        CachedBeaconStateHasher hasher = new();
        dynamic state = gloas ? (object)CreateGloasState(out _, out _) : CreateFuluStateAtBoundary(ValidatorCount);
        if (warm)
            hasher.HashTreeRoot(state);

        target.Resize(state, length);

        AssertParity(() => SszRoots.HashTreeRoot(state), () => hasher.HashTreeRoot(state), field, length);
        target.Resize(state, target.ExactLength);
        Assert.That((Hash256)hasher.HashTreeRoot(state), Is.EqualTo((Hash256)SszRoots.HashTreeRoot(state)), "a refused state must not poison the caches");
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

    private static Hash256[] RootVector(object state, string field) => (Hash256[])state.GetType().GetProperty(field)!.GetValue(state)!;

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

    private static IEnumerable<TestCaseData> Cases(List<FixedField> fields, bool gloas, string method)
    {
        foreach (FixedField field in fields)
        {
            foreach (int? length in (int?[])[null, 0, 1, field.ExactLength - 1, field.ExactLength, field.ExactLength + 1, field.ExactLength * 2])
            {
                foreach (bool warm in (bool[])[false, true])
                {
                    yield return new TestCaseData(gloas, field.Name, length, warm).SetName($"{method}({field.Name}, {(length is null ? "null" : length)}, warm: {warm})");
                }
            }
        }
    }

    private sealed record FixedField(string Name, int ExactLength, Func<int?, object?> Create)
    {
        public void Resize(object state, int? length) => state.GetType().GetProperty(Name)!.SetValue(state, Create(length));
    }

    private static readonly List<FixedField> FuluFields =
    [
        new("BlockRoots", (int)Presets.SlotsPerHistoricalRoot, n => Roots(n)),
        new("StateRoots", (int)Presets.SlotsPerHistoricalRoot, n => Roots(n)),
        new("RandaoMixes", (int)Presets.EpochsPerHistoricalVector, n => Roots(n)),
        new("Slashings", (int)Presets.EpochsPerSlashingsVector, n => Words(n)),
        new("JustificationBits", 4, n => Bits(n)),
        new("ProposerLookahead", (int)Presets.ProposerLookaheadSlots, n => Words(n)),
    ];

    private static readonly List<FixedField> GloasFields =
    [
        .. FuluFields,
        new("ExecutionPayloadAvailability", (int)Presets.SlotsPerHistoricalRoot, n => Bits(n)),
        new("BuilderPendingPayments", (int)Presets.BuilderPendingPaymentsLength, n => Items<BuilderPendingPayment>(n)),
        new("PtcWindow", (int)Presets.PtcWindowLength, n => Items<PayloadTimelinessCommittee>(n)),
    ];

    private static Hash256[]? Roots(int? length) => length is null ? null : Enumerable.Repeat(Hash256.Zero, length.Value).ToArray();

    private static ulong[]? Words(int? length) => length is null ? null : Enumerable.Repeat(7UL, length.Value).ToArray();

    private static BitArray? Bits(int? length) => length is null ? null : new BitArray(length.Value, true);

    private static T[]? Items<T>(int? length) where T : new() =>
        length is null ? null : Enumerable.Range(0, length.Value).Select(static _ => new T()).ToArray();
}
