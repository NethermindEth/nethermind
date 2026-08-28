// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
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

        IPAddress? configuredExternalIp = TryGetExternalIpOverride(_networkConfig.ExternalIp, nameof(NetworkConfig.ExternalIp), expectedFamily: null);
        IPAddress? configuredExternalIpV4 = TryGetExternalIpOverride(_networkConfig.ExternalIpV4, nameof(NetworkConfig.ExternalIpV4), AddressFamily.InterNetwork);
        IPAddress? configuredExternalIpV6 = TryGetExternalIpOverride(_networkConfig.ExternalIpV6, nameof(NetworkConfig.ExternalIpV6), AddressFamily.InterNetworkV6);

        // ExternalIpV6 must not become the primary address: IPv4-only consumers would break, and an
        // IPv6-only override would suppress IPv4 auto-detection. ExternalIp can still select IPv6 explicitly.
        IPAddress externalIp = configuredExternalIp
            ?? configuredExternalIpV4
            ?? await ResolveExternalIp(cancellationToken);

        WarnIfPrimaryAndFamilyOverrideDisagree(
            configuredExternalIp,
            configuredExternalIpV4,
            AddressFamily.InterNetwork,
            nameof(NetworkConfig.ExternalIpV4));
        WarnIfPrimaryAndFamilyOverrideDisagree(
            configuredExternalIp,
            configuredExternalIpV6,
            AddressFamily.InterNetworkV6,
            nameof(NetworkConfig.ExternalIpV6));

        if (!IIPResolver.NethermindIp.IsUnspecified(externalIp))
        {
            ThisNodeInfo.AddInfo("External IP  :", $"{externalIp}");
        }

        return new IIPResolver.NethermindIp(localIp, externalIp, configuredExternalIpV4, configuredExternalIpV6);
    }

    private void WarnIfPrimaryAndFamilyOverrideDisagree(
        IPAddress? configuredExternalIp,
        IPAddress? configuredFamilyIp,
        AddressFamily addressFamily,
        string familyConfigName)
    {
        if (configuredExternalIp?.AddressFamily == addressFamily &&
            configuredFamilyIp is not null &&
            !configuredExternalIp.Equals(configuredFamilyIp) &&
            _logger.IsWarn)
        {
            _logger.Warn($"External IP override: {nameof(NetworkConfig.ExternalIp)} = {configuredExternalIp} disagrees with {familyConfigName} = {configuredFamilyIp}. {familyConfigName} takes precedence when that address family is advertised in the ENR, while other consumers use {nameof(NetworkConfig.ExternalIp)}.");
        }
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
                (bool success, IPAddress ip) = await s.TryGetIP();
                if (success)
                {
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

    private IPAddress? TryGetExternalIpOverride(string? ipOverride, string configName, AddressFamily? expectedFamily)
    {
        if (ipOverride is null)
        {
            return null;
        }

        if (!IPAddress.TryParse(ipOverride, out IPAddress? ipAddress))
        {
            if (_logger.IsWarn) _logger.Warn($"External IP override: {nameof(NetworkConfig)}.{configName} = {ipOverride} has incorrect format.");
            return null;
        }

        IPAddress? normalizedIp = IIPResolver.NethermindIp.NormalizeExternalIp(ipAddress, expectedFamily);
        if (normalizedIp is null)
        {
            if (_logger.IsWarn) _logger.Warn($"External IP override: {nameof(NetworkConfig)}.{configName} = {ipOverride} cannot be used as an external IP.");
            return null;
        }

        if (normalizedIp.IsLoopbackOrPrivateOrLinkLocal || normalizedIp.IsMulticast || normalizedIp.IsSpecialUseAddress)
        {
            if (_logger.IsWarn) _logger.Warn($"External IP override: {nameof(NetworkConfig)}.{configName} = {ipOverride} is not a routable public address and may be discarded by peers.");
        }

        if (_logger.IsWarn) _logger.Warn($"Using the external IP override: {nameof(NetworkConfig)}.{configName} = {ipOverride}");
        return normalizedIp;
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
