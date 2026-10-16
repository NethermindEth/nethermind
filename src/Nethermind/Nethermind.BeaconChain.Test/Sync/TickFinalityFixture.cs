// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Reflection;
using Nethermind.BeaconChain.ForkChoice;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Sync;

namespace Nethermind.BeaconChain.Test.Sync;

/// <summary>Inject unrealized finality; only the epoch-boundary tick may realize it.</summary>
internal static class TickFinalityFixture
{
    public static void SetUnrealizedFinality(BlockImporter importer, CheckpointRef checkpoint)
    {
        object runner = typeof(BlockImporter).GetField("_runner", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(importer)!;
        ForkChoiceStore store = (ForkChoiceStore)typeof(ForkChoiceRunner).GetField("_store", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(runner)!;
        store.UpdateUnrealizedCheckpoints(checkpoint, checkpoint);
    }

    public static DateTime SlotStart(BeaconChainSpec spec, ulong slot) =>
        DateTimeOffset.FromUnixTimeSeconds((long)(spec.GenesisTime + slot * spec.SecondsPerSlot)).UtcDateTime;
}
