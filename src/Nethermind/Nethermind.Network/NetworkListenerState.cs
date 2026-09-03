// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Net;
using System.Threading;
using Nethermind.Logging;
using Nethermind.Network.Config;

namespace Nethermind.Network;

/// <summary>
/// Tracks the preferred and successfully bound inbound listener addresses.
/// </summary>
public sealed class NetworkListenerState
{
    private IPAddress? _rlpxAddress;
    private IPAddress? _discoveryAddress;
    private readonly ILogger _logger;

    public NetworkListenerState(INetworkConfig networkConfig, IIPResolver ipResolver, ILogManager logManager)
    {
        _logger = logManager.GetClassLogger<NetworkListenerState>();
        FallbackAddress = ipResolver.Resolve().GetAwaiter().GetResult().LocalIp;
        PreferredAddress = NetworkHelper.GetInboundBindAddress(FallbackAddress, networkConfig.LocalIp);
    }

    internal NetworkListenerState(IPAddress fallbackAddress, IPAddress preferredAddress, ILogManager logManager)
    {
        _logger = logManager.GetClassLogger<NetworkListenerState>();
        FallbackAddress = fallbackAddress;
        PreferredAddress = preferredAddress;
    }

    /// <summary>The resolved address used when an automatic dual-stack bind cannot be established.</summary>
    public IPAddress FallbackAddress { get; }

    /// <summary>The first address that inbound listeners should try to bind.</summary>
    public IPAddress PreferredAddress { get; }

    /// <summary>The address on which the RLPx listener successfully bound, or <see langword="null"/> before it binds.</summary>
    public IPAddress? RlpxAddress => Volatile.Read(ref _rlpxAddress);

    /// <summary>The address on which the discovery listener successfully bound, or <see langword="null"/> before it binds.</summary>
    public IPAddress? DiscoveryAddress => Volatile.Read(ref _discoveryAddress);

    /// <summary>Raised when a successfully bound listener address changes.</summary>
    public event EventHandler? Changed;

    public void SetRlpxAddress(IPAddress? address) => SetAddress(ref _rlpxAddress, address);

    public void SetDiscoveryAddress(IPAddress? address) => SetAddress(ref _discoveryAddress, address);

    private void SetAddress(ref IPAddress? field, IPAddress? address)
    {
        IPAddress? previous = Interlocked.Exchange(ref field, address);
        if (!Equals(previous, address))
        {
            foreach (Delegate subscriber in Changed?.GetInvocationList() ?? [])
            {
                try
                {
                    ((EventHandler)subscriber)(this, EventArgs.Empty);
                }
                catch (Exception e)
                {
                    if (_logger.IsError) _logger.Error($"A {nameof(NetworkListenerState)} change subscriber failed.", e);
                }
            }
        }
    }
}
