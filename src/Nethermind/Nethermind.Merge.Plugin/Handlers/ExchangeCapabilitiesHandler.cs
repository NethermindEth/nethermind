// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using Nethermind.JsonRpc;
using Nethermind.Logging;

namespace Nethermind.Merge.Plugin.Handlers;

public class ExchangeCapabilitiesHandler : IHandler<HashSet<string>, IReadOnlyList<string>>
{
    private readonly ILogger _logger;
    private readonly IRpcCapabilitiesProvider _engineRpcCapabilitiesProvider;
    private IReadOnlyList<string>? _cachedEnabled;

    public ExchangeCapabilitiesHandler(IRpcCapabilitiesProvider engineRpcCapabilitiesProvider, ILogManager logManager)
    {
        ArgumentNullException.ThrowIfNull(logManager);

        _logger = logManager.GetClassLogger<ExchangeCapabilitiesHandler>();
        _engineRpcCapabilitiesProvider = engineRpcCapabilitiesProvider;
    }

    public ResultWrapper<IReadOnlyList<string>> Handle(HashSet<string> methods)
    {
        FrozenDictionary<string, RpcCapabilityOptions> capabilities = _engineRpcCapabilitiesProvider.GetEngineCapabilities();

        List<string>? enabled = _cachedEnabled is null ? new List<string>(capabilities.Count) : null;
        List<string>? missing = null;

        foreach ((string key, RpcCapabilityOptions flags) in capabilities)
        {
            if (flags.IsEnabled())
            {
                enabled?.Add(key);
                if (flags.ShouldWarnIfMissing() && !methods.Contains(key))
                {
                    missing ??= [];
                    missing.Add(key);
                }
            }
        }

        if (enabled is not null) _cachedEnabled = enabled;

        if (missing is { Count: > 0 } && _logger.IsWarn)
            _logger.Warn($"Consensus client missing capabilities: {string.Join(", ", missing)}");

        return ResultWrapper<IReadOnlyList<string>>.Success(_cachedEnabled!);
    }
}
