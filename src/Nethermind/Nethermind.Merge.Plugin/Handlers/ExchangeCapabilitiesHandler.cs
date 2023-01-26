// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.JsonRpc;
using Nethermind.Logging;

namespace Nethermind.Merge.Plugin.Handlers;

public class ExchangeCapabilitiesHandler : IAsyncHandler<IEnumerable<string>, IEnumerable<string>>
{
    private static IEnumerable<string>? _capabilities;
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(1);

    private readonly ILogger _logger;

    public ExchangeCapabilitiesHandler(ILogManager logManager)
    {
        ArgumentNullException.ThrowIfNull(logManager);

        _logger = logManager.GetClassLogger();
    }

    public async Task<ResultWrapper<IEnumerable<string>>> HandleAsync(IEnumerable<string> methods)
    {
        var task = Task.Run(() => CheckCapabilitiesAsync(methods));

        try
        {
            await task.WaitAsync(_timeout);

            return ResultWrapper<IEnumerable<string>>.Success(_capabilities!);
        }
        catch (TimeoutException)
        {
            if (_logger.IsWarn) _logger.Warn($"{nameof(IEngineRpcModule.engine_exchangeCapabilities)} timed out");

            return ResultWrapper<IEnumerable<string>>.Fail("Timed out", ErrorCodes.Timeout);
        }
    }

    private void CheckCapabilitiesAsync(IEnumerable<string> methods)
    {
        _capabilities ??= typeof(IEngineRpcModule).GetMethods()
            .Select(m => m.Name)
            .Where(m => !m.Equals(nameof(IEngineRpcModule.engine_exchangeCapabilities), StringComparison.Ordinal))
            .Order();

        var missing = methods.Except(_capabilities);

        if (missing.Any())
        {
            if (_logger.IsWarn) _logger.Warn($"{ProductInfo.Name} missing capabilities: {string.Join(", ", missing)}");
        }

        missing = _capabilities.Except(methods);

        if (missing.Any())
        {
            if (_logger.IsWarn) _logger.Warn($"Consensus client missing capabilities: {string.Join(", ", missing)}");
        }
    }
}
