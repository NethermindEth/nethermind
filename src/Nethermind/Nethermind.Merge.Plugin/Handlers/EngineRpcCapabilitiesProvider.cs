// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Frozen;
using System.Collections.Generic;
using System.Reflection;
using Nethermind.Core.Specs;
using Nethermind.JsonRpc;
using Nethermind.Merge.Plugin;
using Nethermind.Merge.Plugin.SszRest;
using static Nethermind.JsonRpc.RpcCapabilityOptions;

namespace Nethermind.HealthChecks;

public class EngineRpcCapabilitiesProvider(ISpecProvider specProvider) : IRpcCapabilitiesProvider
{
    // Tables are built once on first access and never mutated afterwards. FrozenDictionary
    // pays a higher build cost for faster reads — fine because each call to GetXxx hits
    // the cached frozen instance and exchangeCapabilities iterates the full table.
    // The Build call itself is benign-racy: two callers may both populate local tables,
    // but Build is pure and the last assignment wins.
    private FrozenDictionary<string, RpcCapabilityOptions>? _jsonRpc;
    private FrozenDictionary<string, RpcCapabilityOptions>? _ssz;
    private FrozenDictionary<string, RpcCapabilityOptions>? _combined;

    /// <summary>JSON-RPC method capabilities only (e.g. <c>engine_newPayloadV1</c>).</summary>
    public FrozenDictionary<string, RpcCapabilityOptions> GetJsonRpcCapabilities()
    {
        EnsureBuilt();
        return _jsonRpc!;
    }

    /// <summary>SSZ-REST path capabilities only (e.g. <c>"POST /engine/v1/payloads"</c>).</summary>
    public FrozenDictionary<string, RpcCapabilityOptions> GetSszRestPaths()
    {
        EnsureBuilt();
        return _ssz!;
    }

    /// <summary>Union of JSON-RPC capabilities and SSZ-REST paths — what
    /// <c>engine_exchangeCapabilities</c> advertises in a single response per spec.</summary>
    public FrozenDictionary<string, RpcCapabilityOptions> GetEngineCapabilities()
    {
        if (_combined is not null) return _combined;
        EnsureBuilt();

        Dictionary<string, RpcCapabilityOptions> combined = new(_jsonRpc!.Count + _ssz!.Count);
        foreach ((string k, RpcCapabilityOptions v) in _jsonRpc) combined[k] = v;
        foreach ((string k, RpcCapabilityOptions v) in _ssz) combined[k] = v;
        return _combined = combined.ToFrozenDictionary();
    }

    private void EnsureBuilt()
    {
        if (_jsonRpc is not null) return;
        Build(specProvider.GetFinalSpec(), out Dictionary<string, RpcCapabilityOptions> json, out Dictionary<string, RpcCapabilityOptions> ssz);
        _jsonRpc = json.ToFrozenDictionary();
        _ssz = ssz.ToFrozenDictionary();
    }

    /// <summary>
    /// Builds the JSON-RPC and SSZ-REST tables in one pass. Each pair shares its
    /// <see cref="RpcCapabilityOptions"/> so the fork-gating logic is expressed once.
    /// </summary>
    private static void Build(IReleaseSpec spec,
        out Dictionary<string, RpcCapabilityOptions> json,
        out Dictionary<string, RpcCapabilityOptions> ssz)
    {
        bool preCancun = !spec.IsEip4844Enabled;
        bool v4 = spec.RequestsEnabled | spec.IsOpIsthmusEnabled;

        Dictionary<string, RpcCapabilityOptions> jsonLocal = [];
        Dictionary<string, RpcCapabilityOptions> sszLocal = [];

        // Configure adds the same options under both the JSON-RPC method name and any
        // SSZ-REST path declared on that method — so updates to the gate stay in sync.
        void Configure(string method, RpcCapabilityOptions options)
        {
            jsonLocal[method] = options;
            ConfigureSsz(method, options);
        }

        void ConfigureSsz(string method, RpcCapabilityOptions options)
        {
            MethodInfo? methodInfo = typeof(IEngineRpcModule).GetMethod(method);
            SszRestAttribute? attribute = methodInfo?.GetCustomAttribute<SszRestAttribute>();
            if (attribute is not null && methodInfo is not null)
                sszLocal[attribute.ToEndpoint(methodInfo).Metadata.Capability] = options;
        }

        // The Merge
        jsonLocal[nameof(IEngineRpcModule.engine_exchangeTransitionConfigurationV1)] = Gate(preCancun);
        Configure(nameof(IEngineRpcModule.engine_forkchoiceUpdatedV1), Enabled);
        Configure(nameof(IEngineRpcModule.engine_getPayloadV1), Enabled);
        Configure(nameof(IEngineRpcModule.engine_newPayloadV1), Enabled);
        Configure(nameof(IEngineRpcModule.engine_getClientVersionV1), Enabled);
        // SSZ-only: engine_exchangeCapabilities is the meta-method (not advertised in the JSON-RPC
        // capabilities list itself), but its SSZ-REST equivalent IS an explicit endpoint.
        ConfigureSsz(nameof(IEngineRpcModule.engine_exchangeCapabilities), Enabled);

        // Shanghai
        Configure(nameof(IEngineRpcModule.engine_forkchoiceUpdatedV2), Gate(spec.WithdrawalsEnabled));
        Configure(nameof(IEngineRpcModule.engine_getPayloadV2), Gate(spec.WithdrawalsEnabled));
        Configure(nameof(IEngineRpcModule.engine_newPayloadV2), Gate(spec.WithdrawalsEnabled));
        Configure(nameof(IEngineRpcModule.engine_getPayloadBodiesByHashV1), Gate(spec.WithdrawalsEnabled));
        Configure(nameof(IEngineRpcModule.engine_getPayloadBodiesByRangeV1), Gate(spec.WithdrawalsEnabled));

        // Cancun
        Configure(nameof(IEngineRpcModule.engine_getPayloadV3), GateWithWarn(spec.IsEip4844Enabled));
        Configure(nameof(IEngineRpcModule.engine_forkchoiceUpdatedV3), GateWithWarn(spec.IsEip4844Enabled));
        Configure(nameof(IEngineRpcModule.engine_newPayloadV3), GateWithWarn(spec.IsEip4844Enabled));
        Configure(nameof(IEngineRpcModule.engine_getBlobsV1), Gate(spec.IsEip4844Enabled));

        // Prague
        Configure(nameof(IEngineRpcModule.engine_getPayloadV4), GateWithWarn(v4));
        Configure(nameof(IEngineRpcModule.engine_newPayloadV4), GateWithWarn(v4));

        // Osaka
        Configure(nameof(IEngineRpcModule.engine_getPayloadV5), GateWithWarn(spec.IsEip7594Enabled));
        Configure(nameof(IEngineRpcModule.engine_getBlobsV2), Gate(spec.IsEip7594Enabled));
        Configure(nameof(IEngineRpcModule.engine_getBlobsV3), Gate(spec.IsEip7594Enabled));

        // Amsterdam
        Configure(nameof(IEngineRpcModule.engine_getPayloadV6), GateWithWarn(spec.IsEip7928Enabled));
        Configure(nameof(IEngineRpcModule.engine_newPayloadV5), GateWithWarn(spec.IsEip7928Enabled));
        Configure(nameof(IEngineRpcModule.engine_forkchoiceUpdatedV4), GateWithWarn(spec.IsEip7843Enabled));
        Configure(nameof(IEngineRpcModule.engine_getPayloadBodiesByHashV2), GateWithWarn(spec.IsEip7928Enabled));
        Configure(nameof(IEngineRpcModule.engine_getPayloadBodiesByRangeV2), GateWithWarn(spec.IsEip7928Enabled));

        json = jsonLocal;
        ssz = sszLocal;
    }

    private static RpcCapabilityOptions Gate(bool enabled) => enabled ? Enabled : None;

    private static RpcCapabilityOptions GateWithWarn(bool enabled) => enabled ? Enabled | WarnIfMissing : None;
}
