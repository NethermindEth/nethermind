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
    private readonly ILogManager _logManager = logManager;

    private readonly Lock _lock = new();
    private Task<NethermindIp>? _resolveTask;

    public ValueTask<NethermindIp> Resolve(CancellationToken cancellationToken = default)
    {
        Task<NethermindIp>? task = Volatile.Read(ref _resolveTask);
        if (task is not null) return new ValueTask<NethermindIp>(task);

        lock (_lock)
        {
            task = _resolveTask ??= ResolveCore(cancellationToken);
        }

        return new ValueTask<NethermindIp>(task);
    }

    private async Task<NethermindIp> ResolveCore(CancellationToken cancellationToken)
    {
        IPAddress localIp;
        try
        {
            localIp = await InitializeLocalIp();
        }
        catch (Exception)
        {
            localIp = IPAddress.Loopback;
        }

        return new NethermindIp(localIp, await ResolveExternalIp(cancellationToken));
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
            yield return new NetworkConfigExternalIPSource(_networkConfig, _logManager);
            yield return new WebIPSource("http://ipv4.icanhazip.com", _logManager);
            yield return new WebIPSource("http://ipv4bot.whatismyipaddress.com", _logManager);
            yield return new WebIPSource("http://checkip.amazonaws.com", _logManager);
            yield return new WebIPSource("http://ipinfo.io/ip", _logManager);
            yield return new WebIPSource("http://api.ipify.org", _logManager);
        }

        try
        {
            foreach (IIPSource s in GetIPSources())
            {
                (bool success, IPAddress ip) = await s.TryGetIP();
                if (success)
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
            yield return new NetworkConfigLocalIPSource(_networkConfig, _logManager);
        }

        try
        {
            foreach (IIPSource s in GetIPSources())
            {
                (bool success, IPAddress ip) = await s.TryGetIP();
                if (success)
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
