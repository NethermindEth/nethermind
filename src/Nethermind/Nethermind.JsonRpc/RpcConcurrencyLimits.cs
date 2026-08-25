// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.JsonRpc;

/// <summary>Resolves the effective per-cost-class concurrency limits from <see cref="IJsonRpcConfig"/>, applying the documented defaults.</summary>
public static class RpcConcurrencyLimits
{
    private const int MinConcurrency = 2;

    public static int GetEvmExecutionConcurrency(this IJsonRpcConfig config) =>
        Math.Max(1, config.EvmExecutionConcurrency ?? config.EthModuleConcurrentInstances ?? Environment.ProcessorCount);

    public static int GetTracingConcurrency(this IJsonRpcConfig config) =>
        Math.Max(1, config.TracingConcurrency ?? Math.Max(MinConcurrency, Environment.ProcessorCount - 2));

    public static int GetProofConcurrency(this IJsonRpcConfig config) =>
        Math.Max(1, config.ProofConcurrency ?? Math.Max(MinConcurrency, Environment.ProcessorCount / 2));

    public static int GetTraceModuleConcurrentInstances(this IJsonRpcConfig config) =>
        Math.Max(1, config.TraceModuleConcurrentInstances ?? config.GetTracingConcurrency());

    public static int GetProofModuleConcurrentInstances(this IJsonRpcConfig config) =>
        Math.Max(1, config.ProofModuleConcurrentInstances ?? config.GetProofConcurrency());
}
