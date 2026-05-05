// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Collections.Generic;
using Nethermind.Core.Specs;
using Nethermind.JsonRpc;
using Nethermind.Merge.Plugin;

namespace Nethermind.HealthChecks;

public class EngineRpcCapabilitiesProvider(ISpecProvider specProvider) : IRpcCapabilitiesProvider
{
    private readonly ConcurrentDictionary<string, (bool Enabled, bool WarnIfMissing)> _capabilities = new();


    public IReadOnlyDictionary<string, (bool Enabled, bool WarnIfMissing)> GetEngineCapabilities()
    {
        if (_capabilities.IsEmpty)
        {
            IReleaseSpec spec = specProvider.GetFinalSpec();

            // The Merge
            _capabilities[nameof(IEngineRpcModule.engine_exchangeTransitionConfigurationV1)] = (true, false);
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
            _capabilities["POST /engine/v1/payloads"] = (true, false);
            _capabilities["POST /engine/v1/forkchoice"] = (true, false);
            _capabilities["POST /engine/v1/capabilities"] = (true, false);
            _capabilities["POST /engine/v1/client/version"] = (true, false);
            _capabilities["POST /engine/v1/transition-configuration"] = (true, false);

            // Shanghai SSZ-REST paths
            _capabilities["POST /engine/v2/payloads"] = (spec.WithdrawalsEnabled, false);
            _capabilities["POST /engine/v2/forkchoice"] = (spec.WithdrawalsEnabled, false);
            _capabilities["GET /engine/v2/payloads/{payload_id}"] = (spec.WithdrawalsEnabled, false);
            _capabilities["POST /engine/v2/payload-bodies/hashes"] = (spec.WithdrawalsEnabled, false);
            _capabilities["GET /engine/v2/payload-bodies/by-range/{start}/{count}"] = (spec.WithdrawalsEnabled, false);

            // Cancun SSZ-REST paths
            _capabilities["POST /engine/v3/payloads"] = (spec.IsEip4844Enabled, spec.IsEip4844Enabled);
            _capabilities["POST /engine/v3/forkchoice"] = (spec.IsEip4844Enabled, spec.IsEip4844Enabled);
            _capabilities["GET /engine/v3/payloads/{payload_id}"] = (spec.IsEip4844Enabled, spec.IsEip4844Enabled);
            _capabilities["POST /engine/v1/blobs"] = (spec.IsEip4844Enabled, false);

            // Prague SSZ-REST paths
            _capabilities["POST /engine/v4/payloads"] = (v4, v4);
            _capabilities["GET /engine/v4/payloads/{payload_id}"] = (v4, v4);

            // Osaka SSZ-REST paths
            _capabilities["GET /engine/v5/payloads/{payload_id}"] = (spec.IsEip7594Enabled, spec.IsEip7594Enabled);
            _capabilities["POST /engine/v2/blobs"] = (spec.IsEip7594Enabled, false);
            _capabilities["POST /engine/v3/blobs"] = (spec.IsEip7594Enabled, false);

            // Amsterdam SSZ-REST paths
            _capabilities["POST /engine/v5/payloads"] = (spec.IsEip7928Enabled, spec.IsEip7928Enabled);
            _capabilities["GET /engine/v6/payloads/{payload_id}"] = (spec.IsEip7928Enabled, spec.IsEip7928Enabled);
            _capabilities["POST /engine/v4/forkchoice"] = (spec.IsEip7843Enabled, spec.IsEip7843Enabled);
            _capabilities["POST /engine/v2/payload-bodies/hashes"] = (spec.IsEip7928Enabled, spec.IsEip7928Enabled);
            _capabilities["GET /engine/v2/payload-bodies/by-range/{start}/{count}"] = (spec.IsEip7928Enabled, spec.IsEip7928Enabled);
        }

        return _capabilities;
    }
}
