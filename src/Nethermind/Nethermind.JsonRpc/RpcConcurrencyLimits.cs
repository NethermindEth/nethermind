// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.JsonRpc;

/// <summary>Resolves the effective per-cost-class admission limits (concurrency and queue-wait budgets) from <see cref="IJsonRpcConfig"/>, applying the documented defaults.</summary>
public static class RpcConcurrencyLimits
{
    private const int MinDerivedConcurrency = 2;

    /// <summary>Ceiling for the processor-count-derived tracing and proof defaults.</summary>
    /// <remarks>
    /// Every tracing or proof module instance is a full block-processing pipeline (its own DI child scope, processor
    /// and world state), created on first use and retained until shutdown. A default that followed a 128-thread box
    /// would let one burst of <c>trace_*</c> traffic pin over a hundred of them for the life of the process, while the
    /// "headroom for block processing" the subtraction stands for is meaningless at that scale.
    /// </remarks>
    internal const int MaxDerivedConcurrency = 16;

    public static int GetEvmExecutionConcurrency(this IJsonRpcConfig config) =>
        Math.Max(1, config.EvmExecutionConcurrency ?? config.EthModuleConcurrentInstances ?? Environment.ProcessorCount);

    public static int GetTracingConcurrency(this IJsonRpcConfig config) =>
        Math.Max(1, config.TracingConcurrency ?? ClampDerived(Environment.ProcessorCount - 2));

    public static int GetProofConcurrency(this IJsonRpcConfig config) =>
        Math.Max(1, config.ProofConcurrency ?? ClampDerived(Environment.ProcessorCount / 2));

    public static int GetTraceModuleConcurrentInstances(this IJsonRpcConfig config) =>
        Math.Max(1, config.TraceModuleConcurrentInstances ?? config.GetTracingConcurrency());

    public static int GetProofModuleConcurrentInstances(this IJsonRpcConfig config) =>
        Math.Max(1, config.ProofModuleConcurrentInstances ?? config.GetProofConcurrency());

    public static int GetTracingMaxQueueWaitMs(this IJsonRpcConfig config) =>
        Math.Max(0, config.TracingMaxQueueWaitMs ?? GetRequestTimeoutWaitBudget(config));

    public static int GetProofMaxQueueWaitMs(this IJsonRpcConfig config) =>
        Math.Max(0, config.ProofMaxQueueWaitMs ?? GetRequestTimeoutWaitBudget(config));

    // A negative Timeout means Timeout.Infinite for the request and its module rental, so it must not collapse into
    // the zero budget that disables queueing.
    private static int GetRequestTimeoutWaitBudget(IJsonRpcConfig config) =>
        config.Timeout < 0 ? int.MaxValue : config.Timeout;

    internal static int ClampDerived(int derivedConcurrency) =>
        Math.Clamp(derivedConcurrency, MinDerivedConcurrency, MaxDerivedConcurrency);
}
