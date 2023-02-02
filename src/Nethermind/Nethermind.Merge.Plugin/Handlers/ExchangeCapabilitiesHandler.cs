// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Core.Specs;
using Nethermind.JsonRpc;
using Nethermind.Logging;

namespace Nethermind.Merge.Plugin.Handlers;

public class ExchangeCapabilitiesHandler : IAsyncHandler<IEnumerable<string>, IEnumerable<string>>
{
    private static IDictionary<string, bool> _capabilities = new ConcurrentDictionary<string, bool>();
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(1);

    private readonly ILogger _logger;

    public ExchangeCapabilitiesHandler(ISpecProvider specProvider, ILogManager logManager)
    {
        ArgumentNullException.ThrowIfNull(specProvider);
        ArgumentNullException.ThrowIfNull(logManager);

        _logger = logManager.GetClassLogger();

        if (_capabilities.Count == 0)
        {
            var spec = specProvider.GetSpec((long.MaxValue, ulong.MaxValue));

            #region The Merge
            _capabilities[nameof(IEngineRpcModule.engine_exchangeTransitionConfigurationV1)] = true;
            _capabilities[nameof(IEngineRpcModule.engine_executionStatus)] = true;
            _capabilities[nameof(IEngineRpcModule.engine_forkchoiceUpdatedV1)] = true;
            _capabilities[nameof(IEngineRpcModule.engine_getPayloadV1)] = true;
            _capabilities[nameof(IEngineRpcModule.engine_newPayloadV1)] = true;
            #endregion

            #region Shanghai
            _capabilities[nameof(IEngineRpcModule.engine_forkchoiceUpdatedV2)] = spec.WithdrawalsEnabled;
            _capabilities[nameof(IEngineRpcModule.engine_getPayloadBodiesByHashV1)] = spec.WithdrawalsEnabled;
            _capabilities[nameof(IEngineRpcModule.engine_getPayloadBodiesByRangeV1)] = spec.WithdrawalsEnabled;
            _capabilities[nameof(IEngineRpcModule.engine_getPayloadV2)] = spec.WithdrawalsEnabled;
            _capabilities[nameof(IEngineRpcModule.engine_newPayloadV2)] = spec.WithdrawalsEnabled;
            #endregion
        }
    }

    public async Task<ResultWrapper<IEnumerable<string>>> HandleAsync(IEnumerable<string> methods)
    {
        var task = Task.Run(() => CheckCapabilities(methods));

        try
        {
            await task.WaitAsync(_timeout);

            return ResultWrapper<IEnumerable<string>>.Success(_capabilities.Keys);
        }
        catch (TimeoutException)
        {
            if (_logger.IsWarn) _logger.Warn($"{nameof(IEngineRpcModule.engine_exchangeCapabilities)} timed out");

            return ResultWrapper<IEnumerable<string>>.Fail("Timed out", ErrorCodes.Timeout);
        }
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
