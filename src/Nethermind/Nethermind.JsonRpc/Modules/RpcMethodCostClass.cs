// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.JsonRpc.Modules;

/// <summary>
/// Cost class of a JSON-RPC method, used by <see cref="RpcAdmissionController"/> to bound work by what it costs
/// rather than by which module implements it.
/// </summary>
public enum RpcMethodCostClass
{
    /// <summary>Cheap reads and everything else; never gated.</summary>
    Default = 0,

    /// <summary>Methods that execute the EVM against overridable state: <c>eth_call</c> and friends.</summary>
    EvmExecution,

    /// <summary>Re-execution with a tracer attached: <c>debug_trace*</c> and <c>trace_*</c>.</summary>
    Tracing,

    /// <summary>Merkle proof generation: <c>proof_*</c> and <c>eth_getProof</c>.</summary>
    Proof,
}

/// <summary>Maps a JSON-RPC method name to its <see cref="RpcMethodCostClass"/>.</summary>
/// <remarks>
/// Classification is by name so that plugin-provided modules fall into the right class without registering
/// anything: a <c>trace_</c> or <c>debug_trace</c> prefix re-executes blocks with a tracer regardless of who
/// implements it. The EVM-execution list is explicit because the <c>eth_</c> namespace mixes sub-millisecond
/// reads with multi-second simulations and only the latter must be bounded; the <c>debug_</c> namespace likewise
/// hides block re-executions behind names without the <c>trace</c> prefix, so those are listed too.
/// <c>flashbots_validateBuilderSubmission*</c> executes a full block yet stays <see cref="RpcMethodCostClass.Default"/>
/// on purpose: the endpoint is opt-in and serves one relay submission per block, and its own module pool
/// (<c>Flashbots.FlashbotsModuleConcurrentInstances</c>) already bounds it.
/// </remarks>
internal static class RpcMethodCostClassifier
{
    public static RpcMethodCostClass Classify(string methodName) => methodName switch
    {
        "eth_call" or "eth_estimateGas" or "eth_createAccessList" or "eth_simulateV1" or "eth_fillTransaction"
            or "debug_simulateV1"
            => RpcMethodCostClass.EvmExecution,
        "eth_getProof" => RpcMethodCostClass.Proof,
        "debug_intermediateRoots" or "debug_executionWitness" => RpcMethodCostClass.Tracing,
        _ when methodName.StartsWith("debug_trace", StringComparison.Ordinal)
            || methodName.StartsWith("debug_standardTrace", StringComparison.Ordinal)
            || methodName.StartsWith("trace_", StringComparison.Ordinal)
            => RpcMethodCostClass.Tracing,
        _ when methodName.StartsWith("proof_", StringComparison.Ordinal) => RpcMethodCostClass.Proof,
        _ => RpcMethodCostClass.Default,
    };
}
