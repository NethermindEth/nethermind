// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.IO;
using Nethermind.BeaconChain.StateTransition;
using NUnit.Framework;
using YamlDotNet.RepresentationModel;

namespace Ethereum.Beacon.Test;

/// <summary>
/// Consensus-spec <c>sanity/slots</c> tests: advance the pre-state by the number of empty slots in
/// <c>slots.yaml</c> via <see cref="SlotProcessing.ProcessSlots"/> (including epoch processing at
/// boundaries) and compare against the post-state.
/// </summary>
[TestFixture]
public class SanitySlotsTests
{
    [TestCaseSource(nameof(SanitySlotsCases))]
    public void Sanity_slots(string casePath)
    {
        ulong slots = ReadSlots(Path.Combine(casePath, "slots.yaml"));
        BeaconStateTestRunner.RunStateTest(casePath, state => SlotProcessing.ProcessSlots(state, state.Slot + slots, new EpochCache()));
    }

    private static ulong ReadSlots(string slotsPath)
    {
        using StreamReader reader = new(slotsPath);
        YamlStream yaml = [];
        yaml.Load(reader);
        return ulong.Parse(((YamlScalarNode)yaml.Documents[0].RootNode).Value!);
    }

    private static IEnumerable<TestCaseData> SanitySlotsCases() =>
        BeaconStateTestRunner.EnumerateCases("fulu", "sanity", "slots");
}
