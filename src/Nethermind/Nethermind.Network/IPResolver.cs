// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.Network.IP;

namespace Nethermind.Network;

public class IPResolver(INetworkConfig networkConfig, ILogManager logManager) : IIPResolver
{
    private readonly ILogger _logger = logManager?.GetClassLogger<IPResolver>() ?? throw new ArgumentNullException(nameof(logManager));
    private readonly INetworkConfig _networkConfig = networkConfig ?? throw new ArgumentNullException(nameof(networkConfig));

    private readonly Lock _lock = new();
    private Task<IIPResolver.NethermindIp>? _resolveTask;

    public ValueTask<IIPResolver.NethermindIp> Resolve(CancellationToken cancellationToken = default)
    {
        Task<IIPResolver.NethermindIp>? task = Volatile.Read(ref _resolveTask);
        if (task is null)
        {
            lock (_lock)
            {
                // Resolve with CancellationToken.None so the cached, shared resolution is never bound to
                // (and faulted by) a single caller's token. Per-call cancellation is honored via WaitAsync below.
                task = _resolveTask ??= ResolveCore(CancellationToken.None);
            }
        }

        return new ValueTask<IIPResolver.NethermindIp>(task.WaitAsync(cancellationToken));
    }

    private async Task<IIPResolver.NethermindIp> ResolveCore(CancellationToken cancellationToken)
    {
        IPAddress localIp;
        try
        {
            localIp = await InitializeLocalIp();
        }
        catch (Exception e)
        {
            if (_logger.IsWarn) _logger.Warn($"Could not resolve local IP, falling back to loopback: {e.Message}");
            localIp = IPAddress.Loopback;
        }

        return new IIPResolver.NethermindIp(localIp, await ResolveExternalIp(cancellationToken));
    }

    private async Task<IPAddress> ResolveExternalIp(CancellationToken cancellationToken)
    {
        const int maxAttempts = 5;
        const int delaySeconds = 2;

        for (int i = 0; i < maxAttempts; i++)
        {
            if (i > 0)
            {
                if (_logger.IsWarn) _logger.Warn($"External IP resolution failed (attempt {i}/{maxAttempts}). Retrying in {delaySeconds}s...");
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds), cancellationToken);
            }

            try
            {
                IPAddress externalIp = await InitializeExternalIp();
                if (!Equals(externalIp, IPAddress.Any) && !Equals(externalIp, IPAddress.None))
                {
                    return externalIp;
                }
            }
            catch (Exception)
            {
                // Will retry or set to None after loop
            }
        }

        if (_logger.IsWarn) _logger.Warn("External IP could not be resolved after all retries. Peers will not be able to connect.");
        return IPAddress.None;
    }

    private async Task<IPAddress> InitializeExternalIp()
    {
        IEnumerable<IIPSource> GetIPSources()
        {
            yield return new NetworkConfigExternalIPSource(_networkConfig, logManager);
            yield return new WebIPSource("http://ipv4.icanhazip.com", logManager);
            yield return new WebIPSource("http://ipv4bot.whatismyipaddress.com", logManager);
            yield return new WebIPSource("http://checkip.amazonaws.com", logManager);
            yield return new WebIPSource("http://ipinfo.io/ip", logManager);
            yield return new WebIPSource("http://api.ipify.org", logManager);
        }

        try
        {
            foreach (IIPSource s in GetIPSources())
            {
                (bool success, IPAddress? ip) = await s.TryGetIP();
                if (success && ip is not null)
                {
                    ThisNodeInfo.AddInfo("External IP  :", $"{ip}");
                    return ip;
                }
            }
        }
        catch (Exception e)
        {
            if (_logger.IsError) _logger.Error("Error while getting external ip", e);
        }

        return IPAddress.Any;
    }

    private async Task<IPAddress> InitializeLocalIp()
    {
        IEnumerable<IIPSource> GetIPSources()
        {
            yield return new NetworkConfigLocalIPSource(_networkConfig, logManager);
        }

        try
        {
            foreach (IIPSource s in GetIPSources())
            {
                (bool success, IPAddress? ip) = await s.TryGetIP();
                if (success && ip is not null)
                {
                    return ip;
                }
            }
        }
        catch (Exception e)
        {
            if (_logger.IsError) _logger.Error("Error while getting local ip", e);
        }

        return IPAddress.Any;
    }
}
