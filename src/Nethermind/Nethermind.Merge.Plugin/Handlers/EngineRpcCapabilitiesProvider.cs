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

    /// <summary>SSZ-REST path capabilities only (e.g. <c>"POST /engine/v2/payloads"</c>).</summary>
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
            RpcCapabilityOptions sszOptions = options & ~WarnIfMissing;
            sszLocal[path] = sszLocal.TryGetValue(path, out RpcCapabilityOptions existing)
                ? existing | sszOptions
                : sszOptions;
        }

        // The Merge
        jsonLocal[nameof(IEngineRpcModule.engine_exchangeTransitionConfigurationV1)] = Gate(preCancun);
        Configure(nameof(IEngineRpcModule.engine_forkchoiceUpdatedV1), SszRestPaths.PostForkchoice, Enabled);
        Configure(nameof(IEngineRpcModule.engine_getPayloadV1), SszRestPaths.GetPayloads, Enabled);
        Configure(nameof(IEngineRpcModule.engine_newPayloadV1), SszRestPaths.PostPayloads, Enabled);
        Configure(nameof(IEngineRpcModule.engine_getClientVersionV1), SszRestPaths.GetIdentity, Enabled);
        // SSZ-only: engine_exchangeCapabilities is the meta-method (not advertised in the JSON-RPC
        // capabilities list itself), but its SSZ-REST equivalent IS an explicit endpoint.
        sszLocal[SszRestPaths.GetCapabilities] = Enabled;

        // Shanghai
        Configure(nameof(IEngineRpcModule.engine_forkchoiceUpdatedV2), SszRestPaths.PostForkchoice, Gate(spec.WithdrawalsEnabled));
        Configure(nameof(IEngineRpcModule.engine_getPayloadV2), SszRestPaths.GetPayloads, Gate(spec.WithdrawalsEnabled));
        Configure(nameof(IEngineRpcModule.engine_newPayloadV2), SszRestPaths.PostPayloads, Gate(spec.WithdrawalsEnabled));
        Configure(nameof(IEngineRpcModule.engine_getPayloadBodiesByHashV1), SszRestPaths.PostBodiesByHash, Gate(spec.WithdrawalsEnabled));
        Configure(nameof(IEngineRpcModule.engine_getPayloadBodiesByRangeV1), SszRestPaths.GetBodiesByRange, Gate(spec.WithdrawalsEnabled));

        // Cancun
        Configure(nameof(IEngineRpcModule.engine_getPayloadV3), SszRestPaths.GetPayloads, GateWithWarn(spec.IsEip4844Enabled));
        Configure(nameof(IEngineRpcModule.engine_forkchoiceUpdatedV3), SszRestPaths.PostForkchoice, GateWithWarn(spec.IsEip4844Enabled));
        Configure(nameof(IEngineRpcModule.engine_newPayloadV3), SszRestPaths.PostPayloads, GateWithWarn(spec.IsEip4844Enabled));
        Configure(nameof(IEngineRpcModule.engine_getBlobsV1), SszRestPaths.PostBlobsV1, Gate(spec.IsEip4844Enabled));

        // Prague
        Configure(nameof(IEngineRpcModule.engine_getPayloadV4), SszRestPaths.GetPayloads, GateWithWarn(v4));
        Configure(nameof(IEngineRpcModule.engine_newPayloadV4), SszRestPaths.PostPayloads, GateWithWarn(v4));

        // Osaka
        Configure(nameof(IEngineRpcModule.engine_getPayloadV5), SszRestPaths.GetPayloads, GateWithWarn(spec.IsEip7594Enabled));
        Configure(nameof(IEngineRpcModule.engine_getBlobsV2), SszRestPaths.PostBlobsV2, Gate(spec.IsEip7594Enabled));
        Configure(nameof(IEngineRpcModule.engine_getBlobsV3), SszRestPaths.PostBlobsV3, Gate(spec.IsEip7594Enabled));
        Configure(nameof(IEngineRpcModule.engine_getBlobsV4), SszRestPaths.PostBlobsV4, Gate(spec.IsEip7594Enabled));

        // Amsterdam
        Configure(nameof(IEngineRpcModule.engine_getPayloadV6), SszRestPaths.GetPayloads, GateWithWarn(spec.IsEip7928Enabled));
        Configure(nameof(IEngineRpcModule.engine_newPayloadV5), SszRestPaths.PostPayloads, GateWithWarn(spec.IsEip7928Enabled));
        Configure(nameof(IEngineRpcModule.engine_forkchoiceUpdatedV4), SszRestPaths.PostForkchoice, GateWithWarn(spec.IsEip7843Enabled));
        Configure(nameof(IEngineRpcModule.engine_getPayloadBodiesByHashV2), SszRestPaths.PostBodiesByHash, GateWithWarn(spec.IsEip7928Enabled));
        Configure(nameof(IEngineRpcModule.engine_getPayloadBodiesByRangeV2), SszRestPaths.GetBodiesByRange, GateWithWarn(spec.IsEip7928Enabled));
        Configure(nameof(IEngineRpcModule.engine_newPayloadWithWitness), SszRestPaths.PostPayloadsWitness, GateWithWarn(spec.IsEip7928Enabled));
        jsonLocal[nameof(IEngineRpcModule.engine_getBlobsV4)] = Gate(spec.IsEip7843Enabled);

        json = jsonLocal;
        ssz = sszLocal;
    }

    private static RpcCapabilityOptions Gate(bool enabled) => enabled ? Enabled : None;

    private static RpcCapabilityOptions GateWithWarn(bool enabled) => enabled ? Enabled | WarnIfMissing : None;
}
