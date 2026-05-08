// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.JsonRpc;
using Nethermind.Logging;

namespace Nethermind.Merge.Plugin.Handlers;

public class ExchangeCapabilitiesHandler : IHandler<HashSet<string>, IReadOnlyList<string>>
{
    private readonly ILogger _logger;
    private readonly IRpcCapabilitiesProvider _engineRpcCapabilitiesProvider;
    // The enabled-capability projection is stable across calls (the provider's
    // dictionary is built once on first access). Cache the result so we only
    // pay for the projection on the first request; later requests still scan
    // for missing capabilities (depends on caller-supplied input).
    private IReadOnlyList<string>? _cachedEnabled;

    public ExchangeCapabilitiesHandler(IRpcCapabilitiesProvider engineRpcCapabilitiesProvider, ILogManager logManager)
    {
        ArgumentNullException.ThrowIfNull(logManager);

        _logger = logManager.GetClassLogger<ExchangeCapabilitiesHandler>();
        _engineRpcCapabilitiesProvider = engineRpcCapabilitiesProvider;
    }

    public ResultWrapper<IReadOnlyList<string>> Handle(HashSet<string> methods)
    {
        IReadOnlyDictionary<string, (bool Enabled, bool WarnIfMissing)> capabilities = _engineRpcCapabilitiesProvider.GetEngineCapabilities();

        // Single pass over capabilities: build the enabled list (only on first call,
        // cached afterwards) and collect missing-but-required entries (every call).
        // O(N) over capabilities × O(1) HashSet lookup per entry — replaces the
        // previous O(N) × O(M) LINQ Any nested scan.
        List<string>? enabled = _cachedEnabled is null ? new List<string>(capabilities.Count) : null;
        List<string>? missing = null;

        foreach ((string key, (bool isEnabled, bool warnIfMissing)) in capabilities)
        {
            if (isEnabled)
            {
                enabled?.Add(key);
                if (warnIfMissing && !methods.Contains(key))
                {
                    missing ??= new List<string>();
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
