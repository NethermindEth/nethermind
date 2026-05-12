// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Taiko.TaikoSpec;
using NUnit.Framework;

namespace Nethermind.Taiko.Test;

/// <summary>
/// Regression tests for the EIP-2124 fork-id walk on Taiko. The transitions added by
/// <see cref="TaikoChainSpecEngineParameters.AddTransitions"/> are folded into the CRC32
/// fork-id chain by <see cref="Nethermind.Network.ForkInfo"/>, so any extra/missing entry
/// here changes the fork-id NMC announces in eth Status — observed against Taiko Internal
/// devnet bootnodes (alethia-reth) as <c>InvalidForkId IncompatibleOrStale</c> disconnects
/// when Shasta=0 / Unzen=0 were folded unconditionally.
/// </summary>
[TestFixture]
public class TaikoChainSpecEngineParametersTests
{
    [Test]
    public void AddTransitions_skips_genesis_time_Shasta_and_Unzen()
    {
        // Mirrors gavin's nmc-devnet.json: Shasta active at genesis, Unzen at a future ts.
        TaikoChainSpecEngineParameters parameters = new()
        {
            OntakeTransition = 0,
            PacayaTransition = 0,
            ShastaTimestamp = 0,
            UnzenTimestamp = 0x69df3615,
        };

        SortedSet<long> blockNumbers = new();
        SortedSet<ulong> timestamps = new();

        parameters.AddTransitions(blockNumbers, timestamps);

        Assert.That(blockNumbers, Is.Empty,
            "Ontake/Pacaya at block 0 must be filtered — EIP-2124 skips activations at or before genesis");
        Assert.That(timestamps, Is.EqualTo(new[] { 0x69df3615ul }),
            "Shasta=0 must be filtered; Unzen at a future ts must be folded");
    }

    [Test]
    public void AddTransitions_includes_post_genesis_block_transitions()
    {
        // Mirrors taiko-alethia.json: Ontake/Pacaya activate post-genesis.
        TaikoChainSpecEngineParameters parameters = new()
        {
            OntakeTransition = 0x836c0,
            PacayaTransition = 0x11CAB0,
            ShastaTimestamp = 0x69CE6BD4,
        };

        SortedSet<long> blockNumbers = new();
        SortedSet<ulong> timestamps = new();

        parameters.AddTransitions(blockNumbers, timestamps);

        Assert.That(blockNumbers, Is.EqualTo(new[] { 0x836c0L, 0x11CAB0L }));
        Assert.That(timestamps, Is.EqualTo(new[] { 0x69CE6BD4ul }));
    }

    [Test]
    public void AddTransitions_handles_all_null_transitions()
    {
        TaikoChainSpecEngineParameters parameters = new();

        SortedSet<long> blockNumbers = new();
        SortedSet<ulong> timestamps = new();

        parameters.AddTransitions(blockNumbers, timestamps);

        Assert.That(blockNumbers, Is.Empty);
        Assert.That(timestamps, Is.Empty);
    }

    [Test]
    public void AddTransitions_filters_genesis_time_Rip7728_and_L1StaticCall()
    {
        TaikoChainSpecEngineParameters parameters = new()
        {
            Rip7728TransitionTimestamp = 0,
            L1StaticCallTransitionTimestamp = 0,
        };

        SortedSet<long> blockNumbers = new();
        SortedSet<ulong> timestamps = new();

        parameters.AddTransitions(blockNumbers, timestamps);

        Assert.That(timestamps, Is.Empty,
            "RIP-7728 and L1StaticCall at genesis must be filtered too");
    }
}
