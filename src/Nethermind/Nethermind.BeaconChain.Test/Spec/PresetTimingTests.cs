// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.Spec;
using Nethermind.Config;
using Nethermind.Core.Exceptions;

namespace Nethermind.BeaconChain.Test.Spec;

public class PresetTimingTests
{
    [Test]
    public void Consensus_slot_timing_must_match_the_compiled_preset([Values(-1, 0, 1)] int slotsOffset, [Values(-1, 0, 1)] int secondsOffset)
    {
        BeaconChainSpec spec = BeaconChainSpec.Mainnet with
        {
            SlotsPerEpoch = (ulong)((long)Presets.SlotsPerEpoch + slotsOffset),
            SecondsPerSlot = (ulong)((long)Presets.SecondsPerSlot + secondsOffset),
        };
        if (slotsOffset == 0 && secondsOffset == 0)
            Assert.That(() => BeaconChainModule.ValidatePreset(spec), Throws.Nothing);
        else
            AssertConflictingConfiguration(() => BeaconChainModule.ValidatePreset(spec));
    }

    // Startup surfaces only InvalidConfigurationException as the operator's message and the configuration exit code.
    private static void AssertConflictingConfiguration(Action validate) =>
        Assert.That(validate, Throws.TypeOf<InvalidConfigurationException>()
            .With.Property(nameof(InvalidConfigurationException.ExitCode)).EqualTo(ExitCodes.ConflictingConfigurations));
}
