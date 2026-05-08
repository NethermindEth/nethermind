// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.JsonRpc;
using Nethermind.Logging;

namespace Nethermind.Merge.Plugin.Handlers;

public class ExchangeCapabilitiesHandler : IHandler<IEnumerable<string>, IReadOnlyList<string>>
{
    private readonly ILogger _logger;
    private readonly IRpcCapabilitiesProvider _engineRpcCapabilitiesProvider;
    // The enabled-capability set is fixed once the provider builds its dictionary,
    // so cache the projected list and avoid rebuilding it on every Handle call.
    private IReadOnlyList<string>? _enabledCache;

    public ExchangeCapabilitiesHandler(IRpcCapabilitiesProvider engineRpcCapabilitiesProvider, ILogManager logManager)
    {
        ArgumentNullException.ThrowIfNull(logManager);

        _logger = logManager.GetClassLogger<ExchangeCapabilitiesHandler>();
        _engineRpcCapabilitiesProvider = engineRpcCapabilitiesProvider;
    }

    public ResultWrapper<IReadOnlyList<string>> Handle(IEnumerable<string> methods)
    {
        IReadOnlyDictionary<string, (bool Enabled, bool WarnIfMissing)> capabilities = _engineRpcCapabilitiesProvider.GetEngineCapabilities();
        CheckCapabilities(methods, capabilities);

        return ResultWrapper<IReadOnlyList<string>>.Success(_enabledCache ??= BuildEnabledList(capabilities));
    }

    private static IReadOnlyList<string> BuildEnabledList(IReadOnlyDictionary<string, (bool Enabled, bool WarnIfMissing)> capabilities)
    {
        List<string> enabled = new(capabilities.Count);
        foreach (KeyValuePair<string, (bool Enabled, bool WarnIfMissing)> kv in capabilities)
            if (kv.Value.Enabled)
                enabled.Add(kv.Key);
        return enabled;
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

        if (missing.Count > 0)
        {
            if (_logger.IsWarn) _logger.Warn($"Consensus client missing capabilities: {string.Join(", ", missing)}");
        }
    }
}
