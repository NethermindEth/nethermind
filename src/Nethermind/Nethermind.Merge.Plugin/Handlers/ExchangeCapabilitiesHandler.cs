// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core.Collections;
using Nethermind.JsonRpc;
using Nethermind.Logging;

namespace Nethermind.Merge.Plugin.Handlers;

public class ExchangeCapabilitiesHandler : IHandler<ISet<string>, IEnumerable<string>>
{
    private readonly ILogger _logger;
    private readonly IRpcCapabilitiesProvider _engineRpcCapabilitiesProvider;

    public ExchangeCapabilitiesHandler(IRpcCapabilitiesProvider engineRpcCapabilitiesProvider, ILogManager logManager)
    {
        ArgumentNullException.ThrowIfNull(logManager);

        _logger = logManager.GetClassLogger();
        _engineRpcCapabilitiesProvider = engineRpcCapabilitiesProvider;
    }

    public ResultWrapper<IEnumerable<string>> Handle(ISet<string> methods)
    {
        IReadOnlyDictionary<string, (bool Enabled, bool WarnIfMissing)> capabilities = _engineRpcCapabilitiesProvider.GetEngineCapabilities();
        CheckCapabilities(methods, capabilities);

        return ResultWrapper<IEnumerable<string>>.Success(capabilities.Where(static x => x.Value.Enabled).Select(static x => x.Key));
    }

    private void CheckCapabilities(ISet<string> methods, IReadOnlyDictionary<string, (bool Enabled, bool WarnIfMissing)> capabilities)
    {
        using ArrayPoolListRef<string?> missing = new(capabilities.Count);

        foreach (KeyValuePair<string, (bool Enabled, bool WarnIfMissing)> capability in capabilities)
        {
            if (!methods.Contains(capability.Key) && capability.Value is { Enabled: true, WarnIfMissing: true })
                missing.Add(capability.Key);
        }

        if (missing.Count > 0)
        {
            if (_logger.IsWarn) _logger.Warn($"Consensus client missing capabilities: {string.Join(", ", missing.AsSpan())}");
        }
    }
}
