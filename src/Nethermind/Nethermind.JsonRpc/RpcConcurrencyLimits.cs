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

    /// <summary>Gets the effective concurrency limit for EVM-executing JSON-RPC methods.</summary>
    /// <returns>The configured limit, or the effective default when it is not configured.</returns>
    public static int GetEvmExecutionConcurrency(this IJsonRpcConfig config) =>
        Math.Max(1, config.EvmExecutionConcurrency ?? config.EthModuleConcurrentInstances ?? Environment.ProcessorCount);

    /// <summary>Gets the effective concurrency limit for tracing JSON-RPC methods.</summary>
    /// <returns>The configured limit, or the processor-count-derived default clamped to the safe range.</returns>
    public static int GetTracingConcurrency(this IJsonRpcConfig config) =>
        Math.Max(1, config.TracingConcurrency ?? ClampDerived(Environment.ProcessorCount - 2));

    /// <summary>Gets the effective concurrency limit for proof-generating JSON-RPC methods.</summary>
    /// <returns>The configured limit, or the processor-count-derived default clamped to the safe range.</returns>
    public static int GetProofConcurrency(this IJsonRpcConfig config) =>
        Math.Max(1, config.ProofConcurrency ?? ClampDerived(Environment.ProcessorCount / 2));

    /// <summary>Gets the effective number of concurrently retained Trace RPC module instances.</summary>
    /// <returns>The configured number, or the effective tracing concurrency limit.</returns>
    public static int GetTraceModuleConcurrentInstances(this IJsonRpcConfig config) =>
        Math.Max(1, config.TraceModuleConcurrentInstances ?? config.GetTracingConcurrency());

    /// <summary>Gets the effective number of concurrently retained Proof RPC module instances.</summary>
    /// <returns>The configured number, or the effective proof concurrency limit.</returns>
    public static int GetProofModuleConcurrentInstances(this IJsonRpcConfig config) =>
        Math.Max(1, config.ProofModuleConcurrentInstances ?? config.GetProofConcurrency());

    /// <summary>Gets the effective tracing admission queue-wait budget in milliseconds.</summary>
    /// <returns>The configured budget, or the request-timeout-derived default.</returns>
    public static int GetTracingMaxQueueWaitMs(this IJsonRpcConfig config) =>
        Math.Max(0, config.TracingMaxQueueWaitMs ?? GetRequestTimeoutWaitBudget(config));

    /// <summary>Gets the effective proof admission queue-wait budget in milliseconds.</summary>
    /// <returns>The configured budget, or the request-timeout-derived default.</returns>
    public static int GetProofMaxQueueWaitMs(this IJsonRpcConfig config) =>
        Math.Max(0, config.ProofMaxQueueWaitMs ?? GetRequestTimeoutWaitBudget(config));

    // Wait budget substituted for a negative (infinite) Timeout: the Timeout default, because an unbounded wait would
    // let an unseeded gate queue every arrival indefinitely, while zero would disable queueing altogether.
    internal const int InfiniteTimeoutWaitBudgetMs = 20_000;

    private static int GetRequestTimeoutWaitBudget(IJsonRpcConfig config) =>
        config.Timeout < 0 ? InfiniteTimeoutWaitBudgetMs : config.Timeout;

    internal static int ClampDerived(int derivedConcurrency) =>
        Math.Clamp(derivedConcurrency, MinDerivedConcurrency, MaxDerivedConcurrency);
}
