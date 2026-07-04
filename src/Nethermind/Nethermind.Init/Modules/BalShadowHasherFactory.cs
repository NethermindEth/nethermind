// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Consensus.Processing;
using Nethermind.Core.Crypto;
using Nethermind.Crypto.Gpu;
using Nethermind.Logging;

namespace Nethermind.Init.Modules;

/// <summary>
/// Chooses the batch-hashing backend for the BAL shadow lane from configuration and formats the GPU segment of the
/// startup capability line.
/// </summary>
/// <remarks>
/// This is the single seam where GPU types are referenced from the init assembly. When GPU offload is requested and an
/// accelerator is available, the shadow lane hashes through a <see cref="ThresholdKeccakBatchHasher"/> that routes large
/// batches to the GPU and everything else to the multi-core CPU backend; otherwise the lane keeps its recursive default
/// (no hasher is returned). A GPU that is requested but unavailable is contained here, not surfaced as an error.
/// </remarks>
public static class BalShadowHasherFactory
{
    /// <summary>Result of backend selection: the hasher to give the shadow (null keeps the recursive path) and the capability text.</summary>
    public readonly record struct Selection(IKeccakBatchHasher? Hasher, string Capability);

    /// <summary>A successfully created GPU backend and its device description.</summary>
    public readonly record struct GpuProbeResult(IKeccakBatchHasher Gpu, string AcceleratorName, long AcceleratorMemoryBytes);

    /// <summary>Selects the shadow backend for production, probing for a GPU via <see cref="GpuKeccakBatchHasher.TryCreate"/>.</summary>
    /// <param name="config">Shadow configuration (GPU flags and threshold).</param>
    /// <param name="logManager">Log manager passed to the threshold router for its backend-fault warning.</param>
    /// <returns>The selected hasher (or null for the recursive path) and the GPU capability line segment.</returns>
    /// <remarks>The probe is gated on Enabled as well as UseGpu: a disabled shadow must never touch GPU drivers.</remarks>
    public static Selection Create(IBalStateRootConfig config, ILogManager logManager) =>
        Select(config.Enabled && config.UseGpu, config.GpuMinBatch, logManager, static () =>
            GpuKeccakBatchHasher.TryCreate(out GpuKeccakBatchHasher? gpu) && gpu is not null
                ? new GpuProbeResult(gpu, gpu.AcceleratorName, gpu.AcceleratorMemoryBytes)
                : null);

    /// <summary>Pure selection logic, injectable GPU probe for testing without real hardware.</summary>
    /// <param name="useGpu">Whether GPU offload is requested.</param>
    /// <param name="gpuMinBatch">Minimum batch size routed to the GPU by the threshold router.</param>
    /// <param name="logManager">Log manager passed to the threshold router.</param>
    /// <param name="gpuProbe">Produces a GPU backend + description, or null when none is available.</param>
    /// <returns>The selected hasher (null keeps the recursive path) and the capability line segment.</returns>
    public static Selection Select(bool useGpu, int gpuMinBatch, ILogManager logManager, Func<GpuProbeResult?> gpuProbe)
    {
        if (!useGpu) return new Selection(null, "GPU disabled");

        if (gpuProbe() is not { } probe) return new Selection(null, "GPU requested but unavailable");

        // Large batches to the GPU, small batches (and any GPU fault thereafter) to the multi-core CPU backend.
        IKeccakBatchHasher router = new ThresholdKeccakBatchHasher(probe.Gpu, new ParallelKeccakBatchHasher(), gpuMinBatch, logManager);
        long gib = probe.AcceleratorMemoryBytes / (1024L * 1024 * 1024);
        return new Selection(router, $"GPU: {probe.AcceleratorName} ({gib} GB)");
    }
}
