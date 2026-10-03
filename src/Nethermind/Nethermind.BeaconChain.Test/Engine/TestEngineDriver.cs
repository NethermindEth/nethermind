// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.BeaconChain.DataAvailability;
using Nethermind.BeaconChain.Engine;
using Nethermind.BeaconChain.ForkChoice;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Sync;
using Nethermind.BeaconChain.Types;
using Nethermind.Core;
using Nethermind.Logging;

namespace Nethermind.BeaconChain.Test.Engine;

/// <summary>Builds an <see cref="EngineDriver"/> on a Sepolia clock, for tests that do not exercise the fork-dependent forkchoice call.</summary>
internal static class TestEngineDriver
{
    public static readonly BeaconChainSpec Spec = BeaconChainSpec.Sepolia;

    /// <summary>A driver whose clock stands at <paramref name="slot"/> and whose node identity is <paramref name="custody"/>.</summary>
    public static EngineDriver Create(ExternalClDetector detector, ulong slot = 0, NodeColumnCustody? custody = null, ILogManager? logManager = null, TimeSpan? forkchoiceTimeout = null) =>
        new(detector, logManager ?? LimboLogs.Instance, ClockAt(slot), Spec, new FixedCustodySource(custody)) { ForkchoiceTimeout = forkchoiceTimeout ?? TimeSpan.FromSeconds(8) };

    private static SlotClock ClockAt(ulong slot) =>
        new(Spec, new ManualTimestamper(DateTimeOffset.FromUnixTimeSeconds((long)(Spec.GenesisTime + slot * Spec.SecondsPerSlot)).UtcDateTime));

    internal sealed class FixedCustodySource(NodeColumnCustody? custody) : INodeColumnCustodySource
    {
        public NodeColumnCustody? Current => custody;
    }

    internal sealed class BodyOnlyNotifier : INewPayloadNotifier
    {
        public ExecutionStatus NotifyNewPayload(BeaconBlockBody body) => ExecutionStatus.Valid;
    }
}
