// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using Nethermind.Core.Crypto;
using Nethermind.Init.Modules;
using Nethermind.Logging;
using NUnit.Framework;

namespace Nethermind.Consensus.Test.Processing;

/// <summary>
/// Pins the GPU/CPU backend selection for the BAL shadow lane: which backend is chosen and what capability text is
/// produced, across UseGpu off, UseGpu on with no device, and UseGpu on with a device.
/// </summary>
[TestFixture]
public class BalShadowHasherSelectionTests
{
    // A no-op batch hasher standing in for a real GPU backend; identity is all the selection test observes.
    private sealed class FakeGpuHasher : IKeccakBatchHasher
    {
        public void HashBatch(ReadOnlySpan<byte> flat, ReadOnlySpan<int> offsets, Span<ValueHash256> outputs) { }
    }

    [Test]
    public void UseGpu_false_keeps_recursive_path_and_reports_disabled()
    {
        BalShadowHasherFactory.Selection selection = BalShadowHasherFactory.Select(
            useGpu: false, gpuMinBatch: 4096, LimboLogs.Instance,
            gpuProbe: () => throw new AssertionException("GPU must not be probed when UseGpu is false"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(selection.Hasher, Is.Null, "no hasher -> shadow uses the recursive path");
            Assert.That(selection.Capability, Is.EqualTo("GPU disabled"));
        }
    }

    [Test]
    public void UseGpu_true_but_no_device_keeps_recursive_path_and_reports_unavailable()
    {
        BalShadowHasherFactory.Selection selection = BalShadowHasherFactory.Select(
            useGpu: true, gpuMinBatch: 4096, LimboLogs.Instance,
            gpuProbe: () => null); // TryCreate failed / no non-CPU device

        using (Assert.EnterMultipleScope())
        {
            Assert.That(selection.Hasher, Is.Null, "no hasher -> shadow uses the recursive path");
            Assert.That(selection.Capability, Is.EqualTo("GPU requested but unavailable"));
        }
    }

    [Test]
    public void UseGpu_true_with_device_uses_threshold_router_and_reports_device()
    {
        const long memBytes = 96L * 1024 * 1024 * 1024; // 96 GiB
        BalShadowHasherFactory.Selection selection = BalShadowHasherFactory.Select(
            useGpu: true, gpuMinBatch: 4096, LimboLogs.Instance,
            gpuProbe: () => new BalShadowHasherFactory.GpuProbeResult(new FakeGpuHasher(), "Test GPU", memBytes));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(selection.Hasher, Is.InstanceOf<ThresholdKeccakBatchHasher>(), "GPU present -> threshold router");
            Assert.That(selection.Capability, Is.EqualTo("GPU: Test GPU (96 GB)"));
        }
    }
}
