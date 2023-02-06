// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core.Specs;
using Nethermind.JsonRpc;
using Nethermind.Logging;

namespace Nethermind.Merge.Plugin.Handlers;

public class ExchangeCapabilitiesHandler : IHandler<IEnumerable<string>, IEnumerable<string>>
{
    private static IDictionary<string, bool> _capabilities = null!;
    private readonly ILogger _logger;

    public ExchangeCapabilitiesHandler(ISpecProvider specProvider, ILogManager logManager)
    {
        ArgumentNullException.ThrowIfNull(specProvider);
        ArgumentNullException.ThrowIfNull(logManager);

        _logger = logManager.GetClassLogger();

        if (_capabilities is null)
        {
            var spec = specProvider.GetSpec((long.MaxValue, ulong.MaxValue));

            _capabilities = new Dictionary<string, bool>
            {
                #region The Merge
                [nameof(IEngineRpcModule.engine_exchangeTransitionConfigurationV1)] = true,
                [nameof(IEngineRpcModule.engine_executionStatus)] = true,
                [nameof(IEngineRpcModule.engine_forkchoiceUpdatedV1)] = true,
                [nameof(IEngineRpcModule.engine_getPayloadV1)] = true,
                [nameof(IEngineRpcModule.engine_newPayloadV1)] = true,
                #endregion

                #region Shanghai
                [nameof(IEngineRpcModule.engine_forkchoiceUpdatedV2)] = spec.WithdrawalsEnabled,
                [nameof(IEngineRpcModule.engine_getPayloadBodiesByHashV1)] = spec.WithdrawalsEnabled,
                [nameof(IEngineRpcModule.engine_getPayloadBodiesByRangeV1)] = spec.WithdrawalsEnabled,
                [nameof(IEngineRpcModule.engine_getPayloadV2)] = spec.WithdrawalsEnabled,
                [nameof(IEngineRpcModule.engine_newPayloadV2)] = spec.WithdrawalsEnabled
                #endregion
            };
        }
    }

    public ResultWrapper<IEnumerable<string>> Handle(IEnumerable<string> methods)
    {
        CheckCapabilities(methods);

        return ResultWrapper<IEnumerable<string>>.Success(_capabilities.Keys);
    }

    private void CheckCapabilities(IEnumerable<string> methods)
    {
        var missing = new List<string>();

        foreach (var capability in _capabilities)
        {
            var found = false;

            foreach (var method in methods)
                if (method.Equals(capability.Key, StringComparison.Ordinal))
                {
                    found = true;
                    break;
                }

            // Warn if not found and capability activated
            if (!found && capability.Value)
                missing.Add(capability.Key);
        }

        if (missing.Any())
        {
            if (_logger.IsWarn) _logger.Warn($"Consensus client missing capabilities: {string.Join(", ", missing)}");
        }
    }
}
