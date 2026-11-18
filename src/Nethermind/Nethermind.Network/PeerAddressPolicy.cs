// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Logging;

namespace Nethermind.Network;

/// <summary>Controls discovery, dialing and persistence of non-public peer addresses.</summary>
public sealed class PeerAddressPolicy : IDisposable
{
    private readonly IIPResolver? _resolver;
    private int _allowedAddresses = -1;
    private readonly Lock _snapshotLock = new();
    private bool _receivedSnapshot;
    private readonly LocalAddressSource _localAddresses;

    /// <summary>Creates a policy from the node's resolved addresses and local interfaces.</summary>
    public PeerAddressPolicy(IIPResolver resolver, LocalAddressSource localAddresses)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(localAddresses);
        _resolver = resolver;
        _localAddresses = localAddresses;
        _resolver.Resolved += OnResolved;
        _ = ObserveInitialResolution();
    }

    internal PeerAddressPolicy(IIPResolver.NethermindIp addresses, LocalAddressSource localAddresses)
    {
        ArgumentNullException.ThrowIfNull(localAddresses);
        _allowedAddresses = GetSnapshot(addresses.ExternalIp);
        _localAddresses = localAddresses;
    }

    /// <summary>Address classes reachable on the node's advertised network.</summary>
    /// <remarks>Unresolved external addresses fall back only to private LAN or ULA interfaces.</remarks>
    public PeerAddressPermissions AllowedAddresses
    {
        get
        {
            int permissions = Volatile.Read(ref _allowedAddresses);
            if (permissions >= 0) return (PeerAddressPermissions)permissions;
            return _localAddresses.HasPrivateInterface ? PeerAddressPermissions.Private : PeerAddressPermissions.None;
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_resolver is not null) _resolver.Resolved -= OnResolved;
    }

    /// <remarks>Observes startup resolution in the background so policy reads never wait for external lookups.</remarks>
    private async Task ObserveInitialResolution()
    {
        try
        {
            IIPResolver.NethermindIp result = await _resolver!.Resolve();
            lock (_snapshotLock)
            {
                if (!_receivedSnapshot) Volatile.Write(ref _allowedAddresses, GetSnapshot(result.ExternalIp));
            }
        }
        catch (Exception exception)
        {
            _localAddresses.LogResolutionFailure(exception);
        }
    }

    private void OnResolved(object? sender, IIPResolver.NethermindIp addresses)
    {
        _localAddresses.Refresh();
        int permissions = GetSnapshot(addresses.ExternalIp);
        lock (_snapshotLock)
        {
            _receivedSnapshot = true;
            Volatile.Write(ref _allowedAddresses, permissions);
        }
    }

    private static int GetSnapshot(IPAddress address)
        => ParsedIPAddress.Parse(address).IsWildcardOrNone ? -1 : (int)GetPermissions(address);

    internal static bool IsPrivateInterface(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        Span<byte> bytes = stackalloc byte[16];
        address.TryWriteBytes(bytes, out int length);
        return length == 4
            ? bytes[0] == 10 || bytes[0] == 172 && bytes[1] is >= 16 and <= 31 || bytes[0] == 192 && bytes[1] == 168
            : (bytes[0] & 0xfe) == 0xfc;
    }

    internal static bool AllowsNonRoutable(IPAddress localAddress)
    {
        ParsedIPAddress parsed = ParsedIPAddress.Parse(localAddress);
        return !parsed.IsWildcardOrNone && parsed.IsLoopbackOrPrivateOrLinkLocal;
    }

    internal static PeerAddressPermissions GetPermissions(IPAddress address)
        => IsPrivateInterface(address) ? PeerAddressPermissions.Private
            : IsLoopbackOrLinkLocal(address) ? PeerAddressPermissions.All : PeerAddressPermissions.None;

    private static bool IsLoopbackOrLinkLocal(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        ParsedIPAddress parsed = ParsedIPAddress.Parse(address);
        return IPAddress.IsLoopback(address) || address.IsIPv6LinkLocal ||
               parsed.Family == IpFamily.IPv4 && (parsed.V4 & 0xffff0000) == 0xa9fe0000;
    }

    internal static bool IsAcceptable(IPAddress address, PeerAddressPermissions permissions)
    {
        ParsedIPAddress parsed = ParsedIPAddress.Parse(address);
        return !parsed.IsWildcardOrNone && !parsed.IsMulticast && !parsed.IsSpecialUseAddress &&
               (!parsed.IsLoopbackOrPrivateOrLinkLocal ||
                (IsPrivateInterface(address)
                    ? (permissions & PeerAddressPermissions.Private) != 0
                    : (permissions & PeerAddressPermissions.LoopbackAndLinkLocal) != 0));
    }

    internal static bool IsSelfEndpoint(IPEndPoint remote, IPAddress localAddress, int localPort)
        => remote.Port == localPort && remote.Address.NormalizeMappedIPv4().Equals(localAddress.NormalizeMappedIPv4());
}

/// <summary>Reads and caches whether the host has a private network interface.</summary>
public sealed class LocalAddressSource
{
    private static readonly TimeSpan FailureRetryDelay = TimeSpan.FromMinutes(1);
    private readonly Func<IEnumerable<IPAddress>> _enumerate;
    private readonly ILogger _logger;
    private readonly TimeProvider _timeProvider;
    private readonly Lock _lock = new();
    private int _privateInterfaceState;
    private bool _retryPending;
    private long _retryStarted;

    /// <summary>Creates a source backed by the operating system's network interfaces.</summary>
    public LocalAddressSource(ILogManager logManager) : this(logManager, NetworkInterface.GetAllNetworkInterfaces) { }

    internal LocalAddressSource(ILogManager logManager, Func<IEnumerable<NetworkInterface>> enumerateNetworks)
        : this(logManager, () => EnumerateAddresses(enumerateNetworks))
        => ArgumentNullException.ThrowIfNull(enumerateNetworks);

    internal LocalAddressSource(ILogManager logManager, Func<IEnumerable<IPAddress>> enumerate, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(logManager);
        ArgumentNullException.ThrowIfNull(enumerate);
        _enumerate = enumerate;
        _logger = logManager.GetClassLogger<LocalAddressSource>();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    internal bool HasPrivateInterface
    {
        get
        {
            int state = Volatile.Read(ref _privateInterfaceState);
            if (state != 0) return state == 2;
            lock (_lock)
            {
                if (_privateInterfaceState != 0) return _privateInterfaceState == 2;
                if (_retryPending && _timeProvider.GetElapsedTime(_retryStarted) < FailureRetryDelay) return false;
                try
                {
                    bool found = FindPrivateInterface();
                    Volatile.Write(ref _privateInterfaceState, found ? 2 : 1);
                    return found;
                }
                catch (Exception exception) when (exception is NetworkInformationException or PlatformNotSupportedException)
                {
                    _retryStarted = _timeProvider.GetTimestamp();
                    _retryPending = true;
                    if (_logger.IsWarn) _logger.Warn($"Could not enumerate local network interfaces: {exception.Message}");
                    return false;
                }
            }
        }
    }

    internal void Refresh()
    {
        lock (_lock)
        {
            Volatile.Write(ref _privateInterfaceState, 0);
        }
    }

    internal void LogResolutionFailure(Exception exception)
        => _logger.DebugError($"Peer address resolution failed: {exception}");

    private bool FindPrivateInterface()
    {
        foreach (IPAddress address in _enumerate())
            if (PeerAddressPolicy.IsPrivateInterface(address)) return true;
        return false;
    }

    private static IEnumerable<IPAddress> EnumerateAddresses(Func<IEnumerable<NetworkInterface>> enumerateNetworks)
    {
        foreach (NetworkInterface network in enumerateNetworks())
        {
            if (network.OperationalStatus != OperationalStatus.Up || network.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            foreach (UnicastIPAddressInformation address in network.GetIPProperties().UnicastAddresses)
                yield return address.Address;
        }
    }
}

/// <summary>Non-public address classes permitted for discovered peers.</summary>
[Flags]
public enum PeerAddressPermissions
{
    /// <summary>Only publicly routable addresses are permitted.</summary>
    None = 0,
    /// <summary>RFC1918 IPv4 and IPv6 unique local addresses are permitted.</summary>
    Private = 1,
    /// <summary>Loopback and link-local addresses are permitted.</summary>
    LoopbackAndLinkLocal = 2,
    /// <summary>All supported non-public address classes are permitted.</summary>
    All = Private | LoopbackAndLinkLocal
}
