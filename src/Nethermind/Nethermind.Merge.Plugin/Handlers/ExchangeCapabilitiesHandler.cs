// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.JsonRpc;
using Nethermind.Logging;

namespace Nethermind.Merge.Plugin.Handlers;

public class ExchangeCapabilitiesHandler : IHandler<IEnumerable<string>, IEnumerable<string>>
{
    private readonly ILogger _logger;
    private readonly IRpcCapabilitiesProvider _engineRpcCapabilitiesProvider;

    public ExchangeCapabilitiesHandler(IRpcCapabilitiesProvider engineRpcCapabilitiesProvider, ILogManager logManager)
    {
        ArgumentNullException.ThrowIfNull(logManager);

        _logger = logManager.GetClassLogger();
        _engineRpcCapabilitiesProvider = engineRpcCapabilitiesProvider;
    }

    public ResultWrapper<IEnumerable<string>> Handle(IEnumerable<string> methods)
    {
        IReadOnlyDictionary<string, (bool Enabled, bool WarnIfMissing)> capabilities = _engineRpcCapabilitiesProvider.GetEngineCapabilities();
        CheckCapabilities(methods, capabilities);

        return ResultWrapper<IEnumerable<string>>.Success(capabilities.Where(x => x.Value.Enabled).Select(x => x.Key));
    }

    private void CheckCapabilities(IEnumerable<string> methods, IReadOnlyDictionary<string, (bool Enabled, bool WarnIfMissing)> capabilities)
    {
        List<string> missing = new();

        foreach (KeyValuePair<string, (bool Enabled, bool WarnIfMissing)> capability in capabilities)
        {
            bool found = false;

            foreach (string method in methods)
                if (method.Equals(capability.Key, StringComparison.Ordinal))
                {
                    found = true;
                    break;
                }

            // Warn if not found and capability activated
            if (!found && capability.Value is { Enabled: true, WarnIfMissing: true })
                missing.Add(capability.Key);
        }

        if (missing.Any())
        {
            if (_logger.IsWarn) _logger.Warn($"Consensus client missing capabilities: {string.Join(", ", missing)}");
        }
    }
}
