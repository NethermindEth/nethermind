// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using NUnit.Framework;
using static Nethermind.BeaconChain.Test.StateTransition.GloasTestFixtures;

namespace Nethermind.BeaconChain.Test.StateTransition;

/// <summary>
/// <see cref="GloasStateClone"/> exists so the importer can freeze a block's post-state for
/// envelope verification while the lineage keeps advancing. The clone is only useful if neither
/// copy can reach the other through a shared, in-place-written field - which is exactly what the
/// state transition is run against here to prove.
/// </summary>
public class GloasStateCloneTests
{
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
        Assert.Multiple(() =>
        {
            Assert.That(SszRoots.HashTreeRoot(source), Is.EqualTo(sourceRoot), "advancing the clone must not reach the source through any shared field");
            Assert.That(SszRoots.HashTreeRoot(clone), Is.Not.EqualTo(sourceRoot), "fixture bug: advancing must actually have changed the clone");
        });

        Hash256 cloneRoot = SszRoots.HashTreeRoot(clone);
        Advance(source, builderSk);
        Assert.That(SszRoots.HashTreeRoot(clone), Is.EqualTo(cloneRoot), "advancing the source must not reach the clone either");
    }

    /// <summary>
    /// A field added to <see cref="BeaconStateGloas"/> and forgotten in the clone would come back
    /// null or zero: every property of the populated fixture must arrive in the clone, either as the
    /// same reference (shared, immutable by convention) or as an equal copy.
    /// </summary>
    [Test]
    public void Clone_carries_every_state_field()
    {
        BeaconStateGloas source = CreateGloasState(out _, out _);
        // The only field the fixture leaves null; a null on both sides would hide an omission.
        source.HistoricalSummaries = [new HistoricalSummary { BlockSummaryRoot = Hash(0x51), StateSummaryRoot = Hash(0x52) }];
        BeaconStateGloas clone = source.Clone();

        foreach (PropertyInfo property in typeof(BeaconStateGloas).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            object? expected = property.GetValue(source);
            object? actual = property.GetValue(clone);
            Assert.That(expected, Is.Not.Null, $"fixture bug: {property.Name} must be populated for this test to see it");
            Assert.That(actual, Is.Not.Null, $"{property.Name} was not cloned");
            Assert.That(SameContent(expected!, actual!), Is.True, $"{property.Name} differs between source and clone");
        }
    }

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
