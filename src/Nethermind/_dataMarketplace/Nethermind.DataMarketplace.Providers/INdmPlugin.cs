using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Logging;

namespace Nethermind.DataMarketplace.Providers
{
    public interface INdmPlugin
    {
        string? Name { get; }
        string? Type { get; }
        Task InitAsync(ILogManager logManager);
        Task<string?> QueryAsync(IEnumerable<string> args);

        Task SubscribeAsync(Action<string> callback, Func<bool> enabled, IEnumerable<string> args,
            CancellationToken? token = null);
    }
}