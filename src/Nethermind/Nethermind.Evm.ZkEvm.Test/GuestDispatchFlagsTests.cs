// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Evm.Tracing;
using NUnit.Framework;

namespace Nethermind.Evm.ZkEvm.Test;

public class GuestDispatchFlagsTests
{
    [Test]
    public void Rejects_unsupported_tracer_capability([Values(
        nameof(ITxTracer.IsTracingInstructions),
        nameof(ITxTracer.IsCancelable),
        nameof(ITxTracer.IsTracingActions),
        nameof(ITxTracer.IsTracingRefunds),
        nameof(ITxTracer.IsTracingAccess),
        nameof(ITxTracer.IsTracingOpLevelStorage),
        nameof(ITxTracer.IsTracingLogs),
        nameof(ITxTracer.IsTracingBlockHash))] string capability)
    {
        using CapabilityTracer tracer = new(capability);

        Assert.Throws<NotSupportedException>(() => DispatchFlags.Validate(tracer));
    }

    [Test]
    public void Accepts_supported_tracer([Values("", nameof(ITxTracer.IsTracingReceipt))] string capability)
    {
        using CapabilityTracer tracer = new(capability);

        Assert.DoesNotThrow(() => DispatchFlags.Validate(tracer));
    }

    /// <remarks>Re-lists ITxTracer so Validate observes this IsCancelable implementation instead of the default interface value.</remarks>
    private sealed class CapabilityTracer : TxTracer, ITxTracer
    {
        public CapabilityTracer(string capability)
        {
            IsTracingInstructions = capability == nameof(IsTracingInstructions);
            IsCancelable = capability == nameof(IsCancelable);
            IsTracingActions = capability == nameof(IsTracingActions);
            IsTracingRefunds = capability == nameof(IsTracingRefunds);
            IsTracingAccess = capability == nameof(IsTracingAccess);
            IsTracingOpLevelStorage = capability == nameof(IsTracingOpLevelStorage);
            IsTracingLogs = capability == nameof(IsTracingLogs);
            IsTracingBlockHash = capability == nameof(IsTracingBlockHash);
            IsTracingReceipt = capability == nameof(IsTracingReceipt);
        }

        public bool IsCancelable { get; }
    }
}
