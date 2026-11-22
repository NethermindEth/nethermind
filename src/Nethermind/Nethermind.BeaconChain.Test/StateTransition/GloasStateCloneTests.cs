// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections;
using System.Reflection;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using static Nethermind.BeaconChain.Test.StateTransition.GloasTestFixtures;

namespace Nethermind.BeaconChain.Test.StateTransition;

[HardTimeout(60_000)]
public class GloasStateCloneTests
{
    [Test]
    public void Exposed_registry_and_scores_cannot_mutate_sibling_or_failed_clones([Values] bool gloas)
    {
        Validator original = new() { EffectiveBalance = 42 };
        Validator[] validators = [original];
        ulong[] scores = [7];
        if (gloas)
            AssertIsolation(new BeaconStateGloas { Validators = validators, InactivityScores = scores }, s => s.Clone(),
                s => s.Validators!, s => s.InactivityScores!, s => s.ValidatorBuffer!, s => s.InactivityScoreBuffer!);
        else
            AssertIsolation(new BeaconStateFulu { Validators = validators, InactivityScores = scores }, s => s.Clone(),
                s => s.Validators!, s => s.InactivityScores!, s => s.ValidatorBuffer!, s => s.InactivityScoreBuffer!);

        void AssertIsolation<T>(T anchor, Func<T, T> clone, Func<T, Validator[]> registry, Func<T, ulong[]> inactivity,
            Func<T, ForkableArray<Validator>> registryBuffer, Func<T, ForkableArray<ulong>> scoreBuffer)
        {
            T parent = clone(anchor);
            T failed = clone(parent);
            T sibling = clone(parent);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(registryBuffer(failed), Is.SameAs(registryBuffer(parent)));
                Assert.That(scoreBuffer(failed), Is.SameAs(scoreBuffer(parent)));
            }
            validators[0] = new Validator();
            scores[0] = 83;
            registry(failed)[0] = new Validator();
            inactivity(failed)[0] = 93;
            // A caller may retain both arrays across a later fork of the same parent.
            Validator[] escaped = registry(parent);
            ulong[] escapedScores = inactivity(parent);
            T later = clone(parent);
            escaped[0] = new Validator();
            escapedScores[0] = 101;

            using IDisposable scope = Assert.EnterMultipleScope();
            Assert.That(registry(sibling)[0], Is.SameAs(original));
            Assert.That(inactivity(sibling)[0], Is.EqualTo(7));
            Assert.That(registry(later)[0], Is.SameAs(original));
            Assert.That(inactivity(later)[0], Is.EqualTo(7));
            Assert.That(registry(parent), Is.SameAs(escaped));
            Assert.That(inactivity(parent), Is.SameAs(escapedScores));
        }
    }

    [Test]
    public void Clone_hashes_identically_and_advancing_either_copy_across_a_block_and_an_epoch_boundary_leaves_the_other_untouched()
    {
        BeaconStateGloas source = CreateGloasState(out Bls.SecretKey builderSk, out _);
        Hash256 sourceRoot = SszRoots.HashTreeRoot(source);

        BeaconStateGloas clone = source.Clone();
        Assert.That(SszRoots.HashTreeRoot(clone), Is.EqualTo(sourceRoot), "a fresh clone must be the same state");

        // Bid, RANDAO, header and root vectors, then the boundary (registry, lookahead, payment and
        // PTC window rotation, availability bits), then a settlement that replaces a Builder element.
        Advance(clone, builderSk);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(SszRoots.HashTreeRoot(source), Is.EqualTo(sourceRoot), "advancing the clone must not reach the source through any shared field");
            Assert.That(SszRoots.HashTreeRoot(clone), Is.Not.EqualTo(sourceRoot), "fixture bug: advancing must actually have changed the clone");
        }

        Hash256 cloneRoot = SszRoots.HashTreeRoot(clone);
        Advance(source, builderSk);
        Assert.That(SszRoots.HashTreeRoot(clone), Is.EqualTo(cloneRoot), "advancing the source must not reach the clone either");
    }

    [Test]
    public void Clone_carries_every_state_field_and_copies_only_in_place_written_fields()
    {
        BeaconStateGloas source = CreateGloasState(out _, out _);
        // The only field the fixture leaves null; a null on both sides would hide an omission.
        source.HistoricalSummaries = [new HistoricalSummary { BlockSummaryRoot = Hash(0x51), StateSummaryRoot = Hash(0x52) }];
        BeaconStateGloas clone = source.Clone();

        foreach (PropertyInfo property in typeof(BeaconStateGloas).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            object? expected = property.GetValue(source);
            object? actual = property.GetValue(clone);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(expected, Is.Not.Null, $"fixture bug: {property.Name} must be populated for this test to see it");
                Assert.That(actual, Is.Not.Null, $"{property.Name} was not cloned");
                Assert.That(SameContent(expected!, actual!), Is.True, $"{property.Name} differs between source and clone");
            }
            if (property.PropertyType.IsValueType)
                continue;
            bool copied = CopiedFields.Contains(property.Name);
            Assert.That(ReferenceEquals(expected, actual), Is.EqualTo(!copied), $"{property.Name} must be {(copied ? "copied" : "shared by reference")}");
            if (copied && expected is Array { Length: > 0 } sourceArray && !sourceArray.GetType().GetElementType()!.IsValueType)
                Assert.That(((Array)actual!).Cast<object?>().Zip(sourceArray.Cast<object?>()).All(static pair => ReferenceEquals(pair.First, pair.Second)), $"{property.Name} must be a shallow copy");
        }
    }

    private static readonly HashSet<string> CopiedFields =
    [
        nameof(BeaconStateGloas.LatestBlockHeader),
        nameof(BeaconStateGloas.BlockRoots),
        nameof(BeaconStateGloas.StateRoots),
        nameof(BeaconStateGloas.Validators),
        nameof(BeaconStateGloas.Balances),
        nameof(BeaconStateGloas.RandaoMixes),
        nameof(BeaconStateGloas.Slashings),
        nameof(BeaconStateGloas.PreviousEpochParticipation),
        nameof(BeaconStateGloas.CurrentEpochParticipation),
        nameof(BeaconStateGloas.JustificationBits),
        nameof(BeaconStateGloas.InactivityScores),
        nameof(BeaconStateGloas.ProposerLookahead),
        nameof(BeaconStateGloas.Builders),
        nameof(BeaconStateGloas.ExecutionPayloadAvailability),
        nameof(BeaconStateGloas.BuilderPendingPayments),
        nameof(BeaconStateGloas.PtcWindow),
    ];

    private static void Advance(BeaconStateGloas state, Bls.SecretKey builderSk)
    {
        EpochCache cache = new();
        SignedExecutionPayloadBid bid = ValidBuilderBid(state, builderSk, builderIndex: 0, value: 3 * Gwei);
        ApplyBlock(state, MinimalBlock(state, bid), cache);
        GloasSlotProcessing.ProcessSlots(state, 2 * Presets.SlotsPerEpoch, cache);
        ApplyBlock(state, MinimalBlock(state, SelfBuildBid(state, parentBlockHash: bid.Message!.BlockHash!, blockHash: Hash(0x9D))), cache);
    }

    private static bool SameContent(object expected, object actual)
    {
        if (ReferenceEquals(expected, actual))
            return true;
        return (expected, actual) switch
        {
            (BitArray a, BitArray b) => a.Cast<bool>().SequenceEqual(b.Cast<bool>()),
            (Array a, Array b) => a.Length == b.Length && a.Cast<object?>().Zip(b.Cast<object?>()).All(static pair => Equals(pair.First, pair.Second)),
            (BeaconBlockHeader a, BeaconBlockHeader b) => SszRoots.HashTreeRoot(a) == SszRoots.HashTreeRoot(b),
            _ => expected.Equals(actual),
        };
    }
}
