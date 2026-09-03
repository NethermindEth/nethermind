// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Logging;
using Nethermind.Network.Config;

namespace Nethermind.Network;

/// <summary>
/// Tracks the preferred and successfully bound inbound listener addresses.
/// </summary>
public sealed class NetworkListenerState
{
    private ListenerBinding? _rlpxBinding;
    private ListenerBinding? _discoveryBinding;
    private readonly ILogger _logger;

    /// <summary>Initializes the shared listener state from the resolved and configured local addresses.</summary>
    /// <remarks>Construction waits for the initial IP resolution because both listeners must use the same fallback address.</remarks>
    /// <param name="networkConfig">The network listener configuration.</param>
    /// <param name="ipResolver">The resolver that supplies the fallback local address.</param>
    /// <param name="logManager">The log manager used to report subscriber failures.</param>
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
    public IPAddress? RlpxAddress => Volatile.Read(ref _rlpxBinding)?.Address;

    /// <summary>The address on which the discovery listener successfully bound, or <see langword="null"/> before it binds.</summary>
    public IPAddress? DiscoveryAddress => Volatile.Read(ref _discoveryBinding)?.Address;

    /// <summary>Raised when a successfully bound listener address changes.</summary>
    public event EventHandler? Changed;

    /// <summary>Publishes the address currently bound by the RLPx listener.</summary>
    /// <param name="address">The bound address, or <see langword="null"/> when no RLPx listener is active.</param>
    public void SetRlpxAddress(IPAddress? address) => SetBinding(ref _rlpxBinding, address is null ? null : new(address));

    /// <summary>Publishes the address currently bound by the discovery listener.</summary>
    /// <param name="address">The bound address, or <see langword="null"/> when no discovery listener is active.</param>
    public void SetDiscoveryAddress(IPAddress? address) => SetBinding(ref _discoveryBinding, address is null ? null : new(address));

    /// <summary>Publishes an RLPx binding and clears it when that same binding closes.</summary>
    /// <param name="address">The successfully bound address.</param>
    /// <param name="closeCompletion">The completion that signals the listener channel has closed.</param>
    /// <returns>A task that completes after the channel completion has been observed.</returns>
    /// <remarks>A replacement binding is not cleared by completion of an older channel, even when both use the same address.</remarks>
    public Task TrackRlpxAddress(IPAddress address, Task closeCompletion) => TrackBinding(address, closeCompletion, isRlpx: true);

    /// <summary>Publishes a discovery binding and clears it when that same binding closes.</summary>
    /// <param name="address">The successfully bound address.</param>
    /// <param name="closeCompletion">The completion that signals the listener channel has closed.</param>
    /// <returns>A task that completes after the channel completion has been observed.</returns>
    /// <remarks>A replacement binding is not cleared by completion of an older channel, even when both use the same address.</remarks>
    public Task TrackDiscoveryAddress(IPAddress address, Task closeCompletion) => TrackBinding(address, closeCompletion, isRlpx: false);

    private void SetBinding(ref ListenerBinding? field, ListenerBinding? binding)
    {
        ListenerBinding? previous = Interlocked.Exchange(ref field, binding);
        if (!Equals(previous?.Address, binding?.Address))
        {
            OnChanged();
        }
    }

    private Task TrackBinding(IPAddress address, Task closeCompletion, bool isRlpx)
    {
        ListenerBinding binding = new(address);
        if (isRlpx)
        {
            SetBinding(ref _rlpxBinding, binding);
        }
        else
        {
            SetBinding(ref _discoveryBinding, binding);
        }

        return closeCompletion.ContinueWith(
            static (_, state) =>
            {
                (NetworkListenerState listenerState, ListenerBinding binding, bool isRlpx) = ((NetworkListenerState, ListenerBinding, bool))state!;
                if (isRlpx)
                {
                    listenerState.ClearBinding(ref listenerState._rlpxBinding, binding);
                }
                else
                {
                    listenerState.ClearBinding(ref listenerState._discoveryBinding, binding);
                }
            },
            (this, binding, isRlpx),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void ClearBinding(ref ListenerBinding? field, ListenerBinding binding)
    {
        if (ReferenceEquals(Interlocked.CompareExchange(ref field, null, binding), binding))
        {
            OnChanged();
        }
    }

    private void OnChanged()
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

    private sealed class ListenerBinding(IPAddress address)
    {
        public IPAddress Address { get; } = address;
    }
}
