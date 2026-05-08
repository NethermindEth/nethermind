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
    private ConcurrentDictionary<string, (bool Enabled, bool WarnIfMissing)>? _jsonRpcCapabilities;
    private ConcurrentDictionary<string, (bool Enabled, bool WarnIfMissing)>? _sszRestPaths;
    private ConcurrentDictionary<string, (bool Enabled, bool WarnIfMissing)>? _combined;

    /// <summary>JSON-RPC method capabilities only (e.g. <c>engine_newPayloadV1</c>).</summary>
    public IReadOnlyDictionary<string, (bool Enabled, bool WarnIfMissing)> GetJsonRpcCapabilities() =>
        _jsonRpcCapabilities ??= BuildJsonRpcCapabilities(specProvider.GetFinalSpec());

    /// <summary>SSZ-REST path capabilities only (e.g. <c>"POST /engine/v1/payloads"</c>).</summary>
    public IReadOnlyDictionary<string, (bool Enabled, bool WarnIfMissing)> GetSszRestPaths() =>
        _sszRestPaths ??= BuildSszRestPaths(specProvider.GetFinalSpec());

    /// <summary>Union of JSON-RPC capabilities and SSZ-REST paths — what
    /// <c>engine_exchangeCapabilities</c> advertises in a single response per spec.</summary>
    public IReadOnlyDictionary<string, (bool Enabled, bool WarnIfMissing)> GetEngineCapabilities()
    {
        if (_combined is not null) return _combined;

        ConcurrentDictionary<string, (bool, bool)> combined = new();
        foreach ((string k, (bool, bool) v) in GetJsonRpcCapabilities()) combined[k] = v;
        foreach ((string k, (bool, bool) v) in GetSszRestPaths()) combined[k] = v;
        return _combined = combined;
    }

    private static ConcurrentDictionary<string, (bool Enabled, bool WarnIfMissing)> BuildJsonRpcCapabilities(IReleaseSpec spec)
    {
        // engine_exchangeTransitionConfigurationV1 was deprecated in Cancun (EIP-4844).
        bool preCancun = !spec.IsEip4844Enabled;
        bool v4 = spec.RequestsEnabled | spec.IsOpIsthmusEnabled;

        ConcurrentDictionary<string, (bool, bool)> caps = new();

        // The Merge
        caps[nameof(IEngineRpcModule.engine_exchangeTransitionConfigurationV1)] = (preCancun, false);
        caps[nameof(IEngineRpcModule.engine_forkchoiceUpdatedV1)] = (true, false);
        caps[nameof(IEngineRpcModule.engine_getPayloadV1)] = (true, false);
        caps[nameof(IEngineRpcModule.engine_newPayloadV1)] = (true, false);
        caps[nameof(IEngineRpcModule.engine_getClientVersionV1)] = (true, false);

        // Shanghai
        caps[nameof(IEngineRpcModule.engine_forkchoiceUpdatedV2)] = (spec.WithdrawalsEnabled, false);
        caps[nameof(IEngineRpcModule.engine_getPayloadBodiesByHashV1)] = (spec.WithdrawalsEnabled, false);
        caps[nameof(IEngineRpcModule.engine_getPayloadBodiesByRangeV1)] = (spec.WithdrawalsEnabled, false);
        caps[nameof(IEngineRpcModule.engine_getPayloadV2)] = (spec.WithdrawalsEnabled, false);
        caps[nameof(IEngineRpcModule.engine_newPayloadV2)] = (spec.WithdrawalsEnabled, false);

        // Cancun
        caps[nameof(IEngineRpcModule.engine_getPayloadV3)] = (spec.IsEip4844Enabled, spec.IsEip4844Enabled);
        caps[nameof(IEngineRpcModule.engine_forkchoiceUpdatedV3)] = (spec.IsEip4844Enabled, spec.IsEip4844Enabled);
        caps[nameof(IEngineRpcModule.engine_newPayloadV3)] = (spec.IsEip4844Enabled, spec.IsEip4844Enabled);
        caps[nameof(IEngineRpcModule.engine_getBlobsV1)] = (spec.IsEip4844Enabled, false);

        // Prague
        caps[nameof(IEngineRpcModule.engine_getPayloadV4)] = (v4, v4);
        caps[nameof(IEngineRpcModule.engine_newPayloadV4)] = (v4, v4);

        // Osaka
        caps[nameof(IEngineRpcModule.engine_getPayloadV5)] = (spec.IsEip7594Enabled, spec.IsEip7594Enabled);
        caps[nameof(IEngineRpcModule.engine_getBlobsV2)] = (spec.IsEip7594Enabled, false);
        caps[nameof(IEngineRpcModule.engine_getBlobsV3)] = (spec.IsEip7594Enabled, false);

        // Amsterdam
        caps[nameof(IEngineRpcModule.engine_getPayloadV6)] = (spec.IsEip7928Enabled, spec.IsEip7928Enabled);
        caps[nameof(IEngineRpcModule.engine_newPayloadV5)] = (spec.IsEip7928Enabled, spec.IsEip7928Enabled);
        caps[nameof(IEngineRpcModule.engine_forkchoiceUpdatedV4)] = (spec.IsEip7843Enabled, spec.IsEip7843Enabled);
        caps[nameof(IEngineRpcModule.engine_getPayloadBodiesByHashV2)] = (spec.IsEip7928Enabled, spec.IsEip7928Enabled);
        caps[nameof(IEngineRpcModule.engine_getPayloadBodiesByRangeV2)] = (spec.IsEip7928Enabled, spec.IsEip7928Enabled);

        return caps;
    }

    private static ConcurrentDictionary<string, (bool Enabled, bool WarnIfMissing)> BuildSszRestPaths(IReleaseSpec spec)
    {
        bool preCancun = !spec.IsEip4844Enabled;
        bool v4 = spec.RequestsEnabled | spec.IsOpIsthmusEnabled;

        ConcurrentDictionary<string, (bool, bool)> paths = new();

        // Always-on SSZ-REST paths (The Merge baseline)
        paths[SszRestPaths.PostV1Payloads] = (true, false);
        paths[SszRestPaths.GetV1Payloads] = (true, false);
        paths[SszRestPaths.PostV1Forkchoice] = (true, false);
        paths[SszRestPaths.PostV1Capabilities] = (true, false);
        paths[SszRestPaths.PostV1ClientVersion] = (true, false);
        paths[SszRestPaths.PostV1TransitionConfig] = (preCancun, false);

        // Shanghai SSZ-REST paths
        paths[SszRestPaths.PostV2Payloads] = (spec.WithdrawalsEnabled, false);
        paths[SszRestPaths.PostV2Forkchoice] = (spec.WithdrawalsEnabled, false);
        paths[SszRestPaths.GetV2Payloads] = (spec.WithdrawalsEnabled, false);
        paths[SszRestPaths.PostV1PayloadBodiesByHash] = (spec.WithdrawalsEnabled, false);
        paths[SszRestPaths.PostV1PayloadBodiesByRange] = (spec.WithdrawalsEnabled, false);

        // Cancun SSZ-REST paths
        paths[SszRestPaths.PostV3Payloads] = (spec.IsEip4844Enabled, spec.IsEip4844Enabled);
        paths[SszRestPaths.PostV3Forkchoice] = (spec.IsEip4844Enabled, spec.IsEip4844Enabled);
        paths[SszRestPaths.GetV3Payloads] = (spec.IsEip4844Enabled, spec.IsEip4844Enabled);
        paths[SszRestPaths.PostV1Blobs] = (spec.IsEip4844Enabled, false);

        // Prague SSZ-REST paths
        paths[SszRestPaths.PostV4Payloads] = (v4, v4);
        paths[SszRestPaths.GetV4Payloads] = (v4, v4);

        // Osaka SSZ-REST paths
        paths[SszRestPaths.GetV5Payloads] = (spec.IsEip7594Enabled, spec.IsEip7594Enabled);
        paths[SszRestPaths.PostV2Blobs] = (spec.IsEip7594Enabled, false);
        paths[SszRestPaths.PostV3Blobs] = (spec.IsEip7594Enabled, false);

        // Amsterdam SSZ-REST paths
        paths[SszRestPaths.PostV5Payloads] = (spec.IsEip7928Enabled, spec.IsEip7928Enabled);
        paths[SszRestPaths.GetV6Payloads] = (spec.IsEip7928Enabled, spec.IsEip7928Enabled);
        paths[SszRestPaths.PostV4Forkchoice] = (spec.IsEip7843Enabled, spec.IsEip7843Enabled);
        paths[SszRestPaths.PostV2PayloadBodiesByHash] = (spec.IsEip7928Enabled, spec.IsEip7928Enabled);
        paths[SszRestPaths.PostV2PayloadBodiesByRange] = (spec.IsEip7928Enabled, spec.IsEip7928Enabled);

        return paths;
    }
}
