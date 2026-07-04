// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Frozen;
using System.Collections.Generic;
using System.Threading;
using Nethermind.Core.Specs;
using Nethermind.JsonRpc;
using Nethermind.Merge.Plugin;
using Nethermind.Merge.Plugin.SszRest.Handlers;
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
        return Volatile.Read(ref _jsonRpc)!;
    }

    /// <summary>SSZ-REST path capabilities only (e.g. <c>"POST /engine/v1/payloads"</c>).</summary>
    public FrozenDictionary<string, RpcCapabilityOptions> GetSszRestPaths()
    {
        EnsureBuilt();
        return Volatile.Read(ref _ssz)!;
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

    /// <summary>
    /// Whether the V4 engine API methods (<c>engine_getPayloadV4</c>, <c>engine_newPayloadV4</c>) are exposed.
    /// Default: L1 condition (post-Pectra execution requests). Plugins may override to add chain-specific triggers
    /// (e.g. OP Isthmus activation) via subclassing.
    /// </summary>
    protected virtual bool IsV4Enabled(IReleaseSpec spec) => spec.RequestsEnabled;

    private void EnsureBuilt()
    {
        if (Volatile.Read(ref _jsonRpc) is not null) return;
        Build(specProvider.GetFinalSpec(), out Dictionary<string, RpcCapabilityOptions> json, out Dictionary<string, RpcCapabilityOptions> ssz);
        Volatile.Write(ref _ssz, ssz.ToFrozenDictionary());
        Volatile.Write(ref _jsonRpc, json.ToFrozenDictionary());
    }

    /// <summary>
    /// Builds the JSON-RPC and SSZ-REST tables in one pass. Each pair shares its
    /// <see cref="RpcCapabilityOptions"/> so the fork-gating logic is expressed once.
    /// </summary>
    private void Build(IReleaseSpec spec,
        out Dictionary<string, RpcCapabilityOptions> json,
        out Dictionary<string, RpcCapabilityOptions> ssz)
    {
        bool preCancun = !spec.IsEip4844Enabled;
        bool v4 = IsV4Enabled(spec);

        Dictionary<string, RpcCapabilityOptions> jsonLocal = [];
        Dictionary<string, RpcCapabilityOptions> sszLocal = [];

        void Configure(string method, string path, RpcCapabilityOptions options)
        {
            jsonLocal[method] = options;
            sszLocal[path] = options & ~WarnIfMissing;
        }

        // The Merge
        jsonLocal[nameof(IEngineRpcModule.engine_exchangeTransitionConfigurationV1)] = Gate(preCancun);
        Configure(nameof(IEngineRpcModule.engine_forkchoiceUpdatedV1), SszRestPaths.PostV1Forkchoice, Enabled);
        Configure(nameof(IEngineRpcModule.engine_getPayloadV1), SszRestPaths.GetV1Payloads, Enabled);
        Configure(nameof(IEngineRpcModule.engine_newPayloadV1), SszRestPaths.PostV1Payloads, Enabled);
        Configure(nameof(IEngineRpcModule.engine_getClientVersionV1), SszRestPaths.PostV1ClientVersion, Enabled);
        // SSZ-only: engine_exchangeCapabilities is the meta-method (not advertised in the JSON-RPC
        // capabilities list itself), but its SSZ-REST equivalent IS an explicit endpoint.
        sszLocal[SszRestPaths.PostV1Capabilities] = Enabled;

        // Shanghai
        Configure(nameof(IEngineRpcModule.engine_forkchoiceUpdatedV2), SszRestPaths.PostV2Forkchoice, Gate(spec.WithdrawalsEnabled));
        Configure(nameof(IEngineRpcModule.engine_getPayloadV2), SszRestPaths.GetV2Payloads, Gate(spec.WithdrawalsEnabled));
        Configure(nameof(IEngineRpcModule.engine_newPayloadV2), SszRestPaths.PostV2Payloads, Gate(spec.WithdrawalsEnabled));
        Configure(nameof(IEngineRpcModule.engine_getPayloadBodiesByHashV1), SszRestPaths.PostV1PayloadBodiesByHash, Gate(spec.WithdrawalsEnabled));
        Configure(nameof(IEngineRpcModule.engine_getPayloadBodiesByRangeV1), SszRestPaths.GetV1PayloadBodiesByRange, Gate(spec.WithdrawalsEnabled));

        // Cancun
        Configure(nameof(IEngineRpcModule.engine_getPayloadV3), SszRestPaths.GetV3Payloads, GateWithWarn(spec.IsEip4844Enabled));
        Configure(nameof(IEngineRpcModule.engine_forkchoiceUpdatedV3), SszRestPaths.PostV3Forkchoice, GateWithWarn(spec.IsEip4844Enabled));
        Configure(nameof(IEngineRpcModule.engine_newPayloadV3), SszRestPaths.PostV3Payloads, GateWithWarn(spec.IsEip4844Enabled));
        Configure(nameof(IEngineRpcModule.engine_getBlobsV1), SszRestPaths.PostV1Blobs, Gate(spec.IsEip4844Enabled));

        // Prague
        Configure(nameof(IEngineRpcModule.engine_getPayloadV4), SszRestPaths.GetV4Payloads, GateWithWarn(v4));
        Configure(nameof(IEngineRpcModule.engine_newPayloadV4), SszRestPaths.PostV4Payloads, GateWithWarn(v4));

        // Osaka
        Configure(nameof(IEngineRpcModule.engine_getPayloadV5), SszRestPaths.GetV5Payloads, GateWithWarn(spec.IsEip7594Enabled));
        Configure(nameof(IEngineRpcModule.engine_getBlobsV2), SszRestPaths.PostV2Blobs, Gate(spec.IsEip7594Enabled));
        Configure(nameof(IEngineRpcModule.engine_getBlobsV3), SszRestPaths.PostV3Blobs, Gate(spec.IsEip7594Enabled));
        Configure(nameof(IEngineRpcModule.engine_getBlobsV4), SszRestPaths.PostV4Blobs, Gate(spec.IsEip7594Enabled));

        // Amsterdam
        Configure(nameof(IEngineRpcModule.engine_getPayloadV6), SszRestPaths.GetV6Payloads, GateWithWarn(spec.IsEip7928Enabled));
        Configure(nameof(IEngineRpcModule.engine_newPayloadV5), SszRestPaths.PostV5Payloads, GateWithWarn(spec.IsEip7928Enabled));
        Configure(nameof(IEngineRpcModule.engine_forkchoiceUpdatedV4), SszRestPaths.PostV4Forkchoice, GateWithWarn(spec.IsEip7843Enabled));
        // EIP-8146 (JSON-RPC only until an SSZ-REST wire shape is specified)
        jsonLocal[nameof(IEngineRpcModule.engine_newPayloadV6)] = GateWithWarn(spec.IsEip8146Enabled);
        jsonLocal[nameof(IEngineRpcModule.engine_notifyBlockAccessListV1)] = GateWithWarn(spec.IsEip8146Enabled);

        Configure(nameof(IEngineRpcModule.engine_getPayloadBodiesByHashV2), SszRestPaths.PostV2PayloadBodiesByHash, GateWithWarn(spec.IsEip7928Enabled));
        Configure(nameof(IEngineRpcModule.engine_getPayloadBodiesByRangeV2), SszRestPaths.GetV2PayloadBodiesByRange, GateWithWarn(spec.IsEip7928Enabled));

        json = jsonLocal;
        ssz = sszLocal;
    }

    private static RpcCapabilityOptions Gate(bool enabled) => enabled ? Enabled : None;

    private static RpcCapabilityOptions GateWithWarn(bool enabled) => enabled ? Enabled | WarnIfMissing : None;
}
