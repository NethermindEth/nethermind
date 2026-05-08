// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Collections.Generic;
using Nethermind.Core.Specs;
using Nethermind.JsonRpc;
using Nethermind.Merge.Plugin;
using Nethermind.Merge.Plugin.SszRest.Handlers;

namespace Nethermind.HealthChecks;

public class EngineRpcCapabilitiesProvider(ISpecProvider specProvider) : IRpcCapabilitiesProvider
{
    private CapabilityTables? _tables;
    private ConcurrentDictionary<string, (bool Enabled, bool WarnIfMissing)>? _combined;

    /// <summary>JSON-RPC method capabilities only (e.g. <c>engine_newPayloadV1</c>).</summary>
    public IReadOnlyDictionary<string, (bool Enabled, bool WarnIfMissing)> GetJsonRpcCapabilities() => Tables.JsonRpc;

    /// <summary>SSZ-REST path capabilities only (e.g. <c>"POST /engine/v1/payloads"</c>).</summary>
    public IReadOnlyDictionary<string, (bool Enabled, bool WarnIfMissing)> GetSszRestPaths() => Tables.Ssz;

    /// <summary>Union of JSON-RPC capabilities and SSZ-REST paths — what
    /// <c>engine_exchangeCapabilities</c> advertises in a single response per spec.</summary>
    public IReadOnlyDictionary<string, (bool Enabled, bool WarnIfMissing)> GetEngineCapabilities()
    {
        if (_combined is not null) return _combined;

        ConcurrentDictionary<string, (bool, bool)> combined = new();
        foreach ((string k, (bool, bool) v) in Tables.JsonRpc) combined[k] = v;
        foreach ((string k, (bool, bool) v) in Tables.Ssz) combined[k] = v;
        return _combined = combined;
    }

    private CapabilityTables Tables => _tables ??= Build(specProvider.GetFinalSpec());

    /// <summary>
    /// Builds the JSON-RPC and SSZ-REST tables in one pass. Each pair shares an
    /// (Enabled, WarnIfMissing) tuple so the fork-gating logic is expressed once.
    /// </summary>
    private static CapabilityTables Build(IReleaseSpec spec)
    {
        bool preCancun = !spec.IsEip4844Enabled;
        bool v4 = spec.RequestsEnabled | spec.IsOpIsthmusEnabled;

        ConcurrentDictionary<string, (bool, bool)> json = new();
        ConcurrentDictionary<string, (bool, bool)> ssz = new();

        void Pair(string method, string path, bool enabled, bool warnIfMissing)
        {
            (bool, bool) entry = (enabled, warnIfMissing);
            json[method] = entry;
            ssz[path] = entry;
        }

        // The Merge
        Pair(nameof(IEngineRpcModule.engine_exchangeTransitionConfigurationV1), SszRestPaths.PostV1TransitionConfig, preCancun, false);
        Pair(nameof(IEngineRpcModule.engine_forkchoiceUpdatedV1), SszRestPaths.PostV1Forkchoice, true, false);
        Pair(nameof(IEngineRpcModule.engine_getPayloadV1), SszRestPaths.GetV1Payloads, true, false);
        Pair(nameof(IEngineRpcModule.engine_newPayloadV1), SszRestPaths.PostV1Payloads, true, false);
        Pair(nameof(IEngineRpcModule.engine_getClientVersionV1), SszRestPaths.PostV1ClientVersion, true, false);
        // SSZ-only: engine_exchangeCapabilities is the meta-method (not advertised in the JSON-RPC
        // capabilities list itself) but its SSZ-REST equivalent IS an explicit endpoint.
        ssz[SszRestPaths.PostV1Capabilities] = (true, false);

        // Shanghai
        Pair(nameof(IEngineRpcModule.engine_forkchoiceUpdatedV2), SszRestPaths.PostV2Forkchoice, spec.WithdrawalsEnabled, false);
        Pair(nameof(IEngineRpcModule.engine_getPayloadV2), SszRestPaths.GetV2Payloads, spec.WithdrawalsEnabled, false);
        Pair(nameof(IEngineRpcModule.engine_newPayloadV2), SszRestPaths.PostV2Payloads, spec.WithdrawalsEnabled, false);
        Pair(nameof(IEngineRpcModule.engine_getPayloadBodiesByHashV1), SszRestPaths.PostV1PayloadBodiesByHash, spec.WithdrawalsEnabled, false);
        Pair(nameof(IEngineRpcModule.engine_getPayloadBodiesByRangeV1), SszRestPaths.PostV1PayloadBodiesByRange, spec.WithdrawalsEnabled, false);

        // Cancun
        Pair(nameof(IEngineRpcModule.engine_getPayloadV3), SszRestPaths.GetV3Payloads, spec.IsEip4844Enabled, spec.IsEip4844Enabled);
        Pair(nameof(IEngineRpcModule.engine_forkchoiceUpdatedV3), SszRestPaths.PostV3Forkchoice, spec.IsEip4844Enabled, spec.IsEip4844Enabled);
        Pair(nameof(IEngineRpcModule.engine_newPayloadV3), SszRestPaths.PostV3Payloads, spec.IsEip4844Enabled, spec.IsEip4844Enabled);
        Pair(nameof(IEngineRpcModule.engine_getBlobsV1), SszRestPaths.PostV1Blobs, spec.IsEip4844Enabled, false);

        // Prague
        Pair(nameof(IEngineRpcModule.engine_getPayloadV4), SszRestPaths.GetV4Payloads, v4, v4);
        Pair(nameof(IEngineRpcModule.engine_newPayloadV4), SszRestPaths.PostV4Payloads, v4, v4);

        // Osaka
        Pair(nameof(IEngineRpcModule.engine_getPayloadV5), SszRestPaths.GetV5Payloads, spec.IsEip7594Enabled, spec.IsEip7594Enabled);
        Pair(nameof(IEngineRpcModule.engine_getBlobsV2), SszRestPaths.PostV2Blobs, spec.IsEip7594Enabled, false);
        Pair(nameof(IEngineRpcModule.engine_getBlobsV3), SszRestPaths.PostV3Blobs, spec.IsEip7594Enabled, false);

        // Amsterdam
        Pair(nameof(IEngineRpcModule.engine_getPayloadV6), SszRestPaths.GetV6Payloads, spec.IsEip7928Enabled, spec.IsEip7928Enabled);
        Pair(nameof(IEngineRpcModule.engine_newPayloadV5), SszRestPaths.PostV5Payloads, spec.IsEip7928Enabled, spec.IsEip7928Enabled);
        Pair(nameof(IEngineRpcModule.engine_forkchoiceUpdatedV4), SszRestPaths.PostV4Forkchoice, spec.IsEip7843Enabled, spec.IsEip7843Enabled);
        Pair(nameof(IEngineRpcModule.engine_getPayloadBodiesByHashV2), SszRestPaths.PostV2PayloadBodiesByHash, spec.IsEip7928Enabled, spec.IsEip7928Enabled);
        Pair(nameof(IEngineRpcModule.engine_getPayloadBodiesByRangeV2), SszRestPaths.PostV2PayloadBodiesByRange, spec.IsEip7928Enabled, spec.IsEip7928Enabled);

        return new CapabilityTables(json, ssz);
    }

    private sealed record CapabilityTables(
        ConcurrentDictionary<string, (bool Enabled, bool WarnIfMissing)> JsonRpc,
        ConcurrentDictionary<string, (bool Enabled, bool WarnIfMissing)> Ssz);
}
