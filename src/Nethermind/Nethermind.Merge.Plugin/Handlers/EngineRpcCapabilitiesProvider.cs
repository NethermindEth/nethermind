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
    private readonly ConcurrentDictionary<string, (bool Enabled, bool WarnIfMissing)> _capabilities = new();


    public IReadOnlyDictionary<string, (bool Enabled, bool WarnIfMissing)> GetEngineCapabilities()
    {
        if (_capabilities.IsEmpty)
        {
            IReleaseSpec spec = specProvider.GetFinalSpec();

            // engine_exchangeTransitionConfigurationV1 was deprecated in Cancun (EIP-4844).
            // Gate it on !IsEip4844Enabled so post-Cancun clients stop advertising it, matching spec.
            bool preCancun = !spec.IsEip4844Enabled;

            // The Merge
            _capabilities[nameof(IEngineRpcModule.engine_exchangeTransitionConfigurationV1)] = (preCancun, false);
            _capabilities[nameof(IEngineRpcModule.engine_forkchoiceUpdatedV1)] = (true, false);
            _capabilities[nameof(IEngineRpcModule.engine_getPayloadV1)] = (true, false);
            _capabilities[nameof(IEngineRpcModule.engine_newPayloadV1)] = (true, false);
            _capabilities[nameof(IEngineRpcModule.engine_getClientVersionV1)] = (true, false);

            // Shanghai
            _capabilities[nameof(IEngineRpcModule.engine_forkchoiceUpdatedV2)] = (spec.WithdrawalsEnabled, false);
            _capabilities[nameof(IEngineRpcModule.engine_getPayloadBodiesByHashV1)] = (spec.WithdrawalsEnabled, false);
            _capabilities[nameof(IEngineRpcModule.engine_getPayloadBodiesByRangeV1)] = (spec.WithdrawalsEnabled, false);
            _capabilities[nameof(IEngineRpcModule.engine_getPayloadV2)] = (spec.WithdrawalsEnabled, false);
            _capabilities[nameof(IEngineRpcModule.engine_newPayloadV2)] = (spec.WithdrawalsEnabled, false);

            // Cancun
            _capabilities[nameof(IEngineRpcModule.engine_getPayloadV3)] = (spec.IsEip4844Enabled, spec.IsEip4844Enabled);
            _capabilities[nameof(IEngineRpcModule.engine_forkchoiceUpdatedV3)] = (spec.IsEip4844Enabled, spec.IsEip4844Enabled);
            _capabilities[nameof(IEngineRpcModule.engine_newPayloadV3)] = (spec.IsEip4844Enabled, spec.IsEip4844Enabled);
            _capabilities[nameof(IEngineRpcModule.engine_getBlobsV1)] = (spec.IsEip4844Enabled, false);

            // Prague
            bool v4 = spec.RequestsEnabled | spec.IsOpIsthmusEnabled;
            _capabilities[nameof(IEngineRpcModule.engine_getPayloadV4)] = (v4, v4);
            _capabilities[nameof(IEngineRpcModule.engine_newPayloadV4)] = (v4, v4);

            // Osaka
            _capabilities[nameof(IEngineRpcModule.engine_getPayloadV5)] = (spec.IsEip7594Enabled, spec.IsEip7594Enabled);
            _capabilities[nameof(IEngineRpcModule.engine_getBlobsV2)] = (spec.IsEip7594Enabled, false);
            _capabilities[nameof(IEngineRpcModule.engine_getBlobsV3)] = (spec.IsEip7594Enabled, false);

            // Amsterdam
            _capabilities[nameof(IEngineRpcModule.engine_getPayloadV6)] = (spec.IsEip7928Enabled, spec.IsEip7928Enabled);
            _capabilities[nameof(IEngineRpcModule.engine_newPayloadV5)] = (spec.IsEip7928Enabled, spec.IsEip7928Enabled);
            _capabilities[nameof(IEngineRpcModule.engine_forkchoiceUpdatedV4)] = (spec.IsEip7843Enabled, spec.IsEip7843Enabled);
            _capabilities[nameof(IEngineRpcModule.engine_getPayloadBodiesByHashV2)] = (spec.IsEip7928Enabled, spec.IsEip7928Enabled);
            _capabilities[nameof(IEngineRpcModule.engine_getPayloadBodiesByRangeV2)] = (spec.IsEip7928Enabled, spec.IsEip7928Enabled);


            // Always-on SSZ-REST paths (The Merge baseline)
            _capabilities[SszRestPaths.PostV1Payloads] = (true, false);
            _capabilities[SszRestPaths.GetV1Payloads] = (true, false);
            _capabilities[SszRestPaths.PostV1Forkchoice] = (true, false);
            _capabilities[SszRestPaths.PostV1Capabilities] = (true, false);
            _capabilities[SszRestPaths.PostV1ClientVersion] = (true, false);
            _capabilities[SszRestPaths.PostV1TransitionConfig] = (preCancun, false);

            // Shanghai SSZ-REST paths
            _capabilities[SszRestPaths.PostV2Payloads] = (spec.WithdrawalsEnabled, false);
            _capabilities[SszRestPaths.PostV2Forkchoice] = (spec.WithdrawalsEnabled, false);
            _capabilities[SszRestPaths.GetV2Payloads] = (spec.WithdrawalsEnabled, false);
            _capabilities[SszRestPaths.PostV1PayloadBodiesByHash] = (spec.WithdrawalsEnabled, false);
            _capabilities[SszRestPaths.PostV1PayloadBodiesByRange] = (spec.WithdrawalsEnabled, false);

            // Cancun SSZ-REST paths
            _capabilities[SszRestPaths.PostV3Payloads] = (spec.IsEip4844Enabled, spec.IsEip4844Enabled);
            _capabilities[SszRestPaths.PostV3Forkchoice] = (spec.IsEip4844Enabled, spec.IsEip4844Enabled);
            _capabilities[SszRestPaths.GetV3Payloads] = (spec.IsEip4844Enabled, spec.IsEip4844Enabled);
            _capabilities[SszRestPaths.PostV1Blobs] = (spec.IsEip4844Enabled, false);

            // Prague SSZ-REST paths
            _capabilities[SszRestPaths.PostV4Payloads] = (v4, v4);
            _capabilities[SszRestPaths.GetV4Payloads] = (v4, v4);

            // Osaka SSZ-REST paths
            _capabilities[SszRestPaths.GetV5Payloads] = (spec.IsEip7594Enabled, spec.IsEip7594Enabled);
            _capabilities[SszRestPaths.PostV2Blobs] = (spec.IsEip7594Enabled, false);
            _capabilities[SszRestPaths.PostV3Blobs] = (spec.IsEip7594Enabled, false);

            // Amsterdam SSZ-REST paths
            _capabilities[SszRestPaths.PostV5Payloads] = (spec.IsEip7928Enabled, spec.IsEip7928Enabled);
            _capabilities[SszRestPaths.GetV6Payloads] = (spec.IsEip7928Enabled, spec.IsEip7928Enabled);
            _capabilities[SszRestPaths.PostV4Forkchoice] = (spec.IsEip7843Enabled, spec.IsEip7843Enabled);
            _capabilities[SszRestPaths.PostV2PayloadBodiesByHash] = (spec.IsEip7928Enabled, spec.IsEip7928Enabled);
            _capabilities[SszRestPaths.PostV2PayloadBodiesByRange] = (spec.IsEip7928Enabled, spec.IsEip7928Enabled);
        }

        return _capabilities;
    }
}
