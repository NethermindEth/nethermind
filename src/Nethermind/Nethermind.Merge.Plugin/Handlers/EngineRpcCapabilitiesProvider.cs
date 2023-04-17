// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Collections.Generic;
using Nethermind.Core.Specs;
using Nethermind.JsonRpc;
using Nethermind.Merge.Plugin;

namespace Nethermind.HealthChecks;

public class EngineRpcCapabilitiesProvider : IRpcCapabilitiesProvider
{
    private static readonly ConcurrentDictionary<string, bool> _capabilities = new();

    private readonly ISpecProvider _specProvider;

    public EngineRpcCapabilitiesProvider(ISpecProvider specProvider)
    {
        _specProvider = specProvider;
    }
    public IReadOnlyDictionary<string, bool> GetEngineCapabilities()
    {
        if (_capabilities.IsEmpty)
        {
            IReleaseSpec spec = _specProvider.GetFinalSpec();

            #region The Merge
            _capabilities[nameof(IEngineRpcModule.engine_exchangeTransitionConfigurationV1)] = true;
            _capabilities[nameof(IEngineRpcModule.engine_forkchoiceUpdatedV1)] = true;
            _capabilities[nameof(IEngineRpcModule.engine_getPayloadV1)] = true;
            _capabilities[nameof(IEngineRpcModule.engine_newPayloadV1)] = true;
            #endregion

            #region Shanghai
            _capabilities[nameof(IEngineRpcModule.engine_forkchoiceUpdatedV2)] = spec.WithdrawalsEnabled;
            _capabilities[nameof(IEngineRpcModule.engine_getPayloadBodiesByHashV1)] = false;
            _capabilities[nameof(IEngineRpcModule.engine_getPayloadBodiesByRangeV1)] = false;
            _capabilities[nameof(IEngineRpcModule.engine_getPayloadV2)] = spec.WithdrawalsEnabled;
            _capabilities[nameof(IEngineRpcModule.engine_newPayloadV2)] = spec.WithdrawalsEnabled;
            #endregion
        }

        return _capabilities;
    }
}
