// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.BeaconChain.DataAvailability;
using Nethermind.BeaconChain.Engine;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Sync;
using Nethermind.Core;
using Nethermind.Logging;

namespace Nethermind.BeaconChain.Test.Engine;

/// <summary>Builds an <see cref="EngineDriver"/> on a Sepolia clock, for tests that do not exercise the fork-dependent forkchoice call.</summary>
internal static class TestEngineDriver
{
    public static readonly BeaconChainSpec Spec = BeaconChainSpec.Sepolia;

    /// <summary>A driver whose clock stands at <paramref name="slot"/> and whose node identity is <paramref name="custody"/>.</summary>
    public static EngineDriver Create(ExternalClDetector detector, ulong slot = 0, NodeColumnCustody? custody = null, ILogManager? logManager = null) =>
        new(detector, logManager ?? LimboLogs.Instance, ClockAt(slot), Spec, new FixedCustodySource(custody));

    private static SlotClock ClockAt(ulong slot) =>
        new(Spec, new ManualTimestamper(DateTimeOffset.FromUnixTimeSeconds((long)(Spec.GenesisTime + slot * Spec.SecondsPerSlot)).UtcDateTime));

    private sealed class FixedCustodySource(NodeColumnCustody? custody) : INodeColumnCustodySource
    {
        public NodeColumnCustody? Current => custody;
    }
}
