// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.Network.Discovery;
using Nethermind.Network.IP;

namespace Nethermind.Network;

public class IPResolver : IIPResolver, IAsyncDisposable
{
    private const int UnresolvedFastAttemptLimit = 5;
    private static readonly TimeSpan ResolutionCacheDuration = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan UnresolvedRetryDelay = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan StartupRetryDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan AutoAddressMaxStaleAge = TimeSpan.FromHours(1);

    private readonly ILogger _logger;
    private readonly ILogManager _logManager;
    private readonly INetworkConfig _networkConfig;
    private readonly Func<AddressFamily, IEnumerable<IIPSource>> _externalIpSources;
    private readonly Func<AddressFamily, bool> _hasLocalAddressFamily;
    private readonly TimeProvider _timeProvider;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ITimer _refreshTimer;

    private readonly Lock _lock = new();
    private Task<IIPResolver.NethermindIp>? _resolveTask;
    // Resolution I/O is serialized: only the initial operation or a refresh can rearm the one-shot timer.
    private Task _resolutionWorker = Task.CompletedTask;
    private Task? _disposeTask;
    private bool _disposed;
    private bool _usesAutomaticResolution;
    private bool _retryUnresolvedResolution;
    private int _consecutiveUnresolvedResolutionAttempts;
    private ConfiguredAddresses? _configured;
    private AutoResolvedIp? _lastExternalIpV4;
    private AutoResolvedIp? _lastExternalIpV6;
    private bool _unresolvedExternalIpWarned;

    /// <inheritdoc/>
    public event EventHandler? Changed;

    public IPResolver(INetworkConfig networkConfig, ILogManager logManager)
        : this(networkConfig, logManager, externalIpSources: null)
    {
    }

    internal IPResolver(
        INetworkConfig networkConfig,
        ILogManager logManager,
        Func<AddressFamily, IEnumerable<IIPSource>>? externalIpSources,
        TimeProvider? timeProvider = null,
        Func<AddressFamily, bool>? hasLocalAddressFamily = null)
    {
        ArgumentNullException.ThrowIfNull(networkConfig);
        ArgumentNullException.ThrowIfNull(logManager);

        _networkConfig = networkConfig;
        _logManager = logManager;
        _logger = logManager.GetClassLogger<IPResolver>();
        _externalIpSources = externalIpSources ?? (family => CreateExternalIpSources(family, logManager));
        _hasLocalAddressFamily = hasLocalAddressFamily ?? HasLocalAddressFamily;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _refreshTimer = _timeProvider.CreateTimer(static state => ((IPResolver)state!).RefreshOnTimer(), this,
            Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public ValueTask<IIPResolver.NethermindIp> Resolve(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed), this);
        Task<IIPResolver.NethermindIp>? task = Volatile.Read(ref _resolveTask);
        if (task is null || task.IsFaulted || task.IsCanceled)
        {
            lock (_lock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                task = _resolveTask;
                if (task is null || task.IsFaulted || task.IsCanceled)
                {
                    // The shared resolution is never bound to one caller's token. Per-call cancellation is
                    // honored by WaitAsync below, without cancelling or faulting the cached operation.
                    TaskCompletionSource<IIPResolver.NethermindIp> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    task = _resolveTask = completion.Task;
                    _resolutionWorker = CompleteResolution(completion);
                }
            }
        }

        return new ValueTask<IIPResolver.NethermindIp>(task.WaitAsync(cancellationToken));
    }

    private void RefreshOnTimer()
    {
        TaskCompletionSource completion;
        IIPResolver.NethermindIp previous;
        lock (_lock)
        {
            if (_disposed || _resolveTask is not { IsCompletedSuccessfully: true } resolved) return;
            previous = resolved.Result;
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _resolutionWorker = completion.Task;
        }

        _ = RefreshResolution(previous, completion);
    }

    private void ScheduleRefresh()
    {
        lock (_lock)
        {
            if (!_disposed && _usesAutomaticResolution)
            {
                _refreshTimer.Change(_retryUnresolvedResolution ? UnresolvedRetryDelay : ResolutionCacheDuration,
                    Timeout.InfiniteTimeSpan);
            }
        }
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        lock (_lock)
        {
            _disposed = true;
            return new ValueTask(_disposeTask ??= DisposeCoreAsync());
        }
    }

    private async Task DisposeCoreAsync()
    {
        await _shutdown.CancelAsync();
        await _refreshTimer.DisposeAsync();
        await _resolutionWorker;
        _shutdown.Dispose();
    }

    private async Task CompleteResolution(TaskCompletionSource<IIPResolver.NethermindIp> completion)
    {
        try
        {
            // Startup consumers such as the enode read the first result once, so it gets the bounded retry.
            IIPResolver.NethermindIp result = await ResolveAndRecord(_shutdown.Token);
            while (_retryUnresolvedResolution)
            {
                await Task.Delay(StartupRetryDelay, _timeProvider, _shutdown.Token);
                result = await ResolveAndRecord(_shutdown.Token);
            }

            completion.SetResult(result);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            completion.SetCanceled(_shutdown.Token);
        }
        catch (Exception e)
        {
            completion.SetException(e);
        }
        finally
        {
            ScheduleRefresh();
        }
    }

    private async Task RefreshResolution(IIPResolver.NethermindIp previous, TaskCompletionSource completion)
    {
        try
        {
            IIPResolver.NethermindIp result = await ResolveAndRecord(_shutdown.Token);
            Volatile.Write(ref _resolveTask, Task.FromResult(result));
            if (result != previous)
            {
                OnChanged();
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            return;
        }
        catch (Exception e)
        {
            // The cached addresses stay in use; the next refresh is attempted after another cache interval.
            if (_logger.IsError) _logger.Error("External IP refresh failed.", e);
        }
        finally
        {
            ScheduleRefresh();
            completion.SetResult();
        }
    }

    private async Task<IIPResolver.NethermindIp> ResolveAndRecord(CancellationToken cancellationToken)
    {
        (IIPResolver.NethermindIp result, bool usedAutomaticResolution) = await ResolveCore(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        bool unresolvedAutomaticResolution = usedAutomaticResolution &&
            result.ExternalIpV4 is null && result.ExternalIpV6 is null;
        _consecutiveUnresolvedResolutionAttempts = unresolvedAutomaticResolution
            ? _consecutiveUnresolvedResolutionAttempts + 1
            : 0;
        _retryUnresolvedResolution = _consecutiveUnresolvedResolutionAttempts is > 0 and < UnresolvedFastAttemptLimit;
        _usesAutomaticResolution = usedAutomaticResolution;
        return result;
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
                if (_logger.IsError) _logger.Error($"An {nameof(IIPResolver)} change subscriber failed.", e);
            }
        }
    }

    private async Task<(IIPResolver.NethermindIp Result, bool UsedAutomaticResolution)> ResolveCore(CancellationToken cancellationToken)
    {
        ConfiguredAddresses configured = _configured ??= await ReadConfiguredAddresses(cancellationToken);
        bool automaticResolutionEnabled = _networkConfig.EnableExternalIpResolution;
        bool resolveExternalIpV4 = automaticResolutionEnabled &&
            configured.ExternalIpV4 is null &&
            _hasLocalAddressFamily(AddressFamily.InterNetwork);
        bool resolveExternalIpV6 = automaticResolutionEnabled &&
            configured.ExternalIpV6 is null &&
            _hasLocalAddressFamily(AddressFamily.InterNetworkV6);

        Task<IPAddress?> externalIpV4Task = resolveExternalIpV4
            ? ResolveAutomaticExternalIp(AddressFamily.InterNetwork, cancellationToken)
            : Task.FromResult(configured.ExternalIpV4);
        Task<IPAddress?> externalIpV6Task = resolveExternalIpV6
            ? ResolveAutomaticExternalIp(AddressFamily.InterNetworkV6, cancellationToken)
            : Task.FromResult(configured.ExternalIpV6);

        await Task.WhenAll(externalIpV4Task, externalIpV6Task);
        IPAddress? externalIpV4 = await externalIpV4Task;
        IPAddress? externalIpV6 = await externalIpV6Task;

        IPAddress externalIp = configured.ExternalIp
            ?? externalIpV4
            ?? externalIpV6
            ?? IPAddress.None;

        if (!externalIp.IsWildcardOrNone)
        {
            ThisNodeInfo.AddInfo("External IP  :", $"{externalIp}");
        }

        bool unresolved = externalIpV4 is null && externalIpV6 is null;
        if (unresolved && !_unresolvedExternalIpWarned && _logger.IsWarn)
        {
            _logger.Warn("External IP could not be resolved. Peers will not be able to connect.");
        }

        _unresolvedExternalIpWarned = unresolved;

        return (
            new IIPResolver.NethermindIp(configured.LocalIp, externalIp, externalIpV4, externalIpV6),
            automaticResolutionEnabled && (configured.ExternalIpV4 is null || configured.ExternalIpV6 is null));
    }

    private async Task<ConfiguredAddresses> ReadConfiguredAddresses(CancellationToken cancellationToken)
    {
        IPAddress localIp;
        try
        {
            localIp = await InitializeLocalIp(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception e)
        {
            if (_logger.IsWarn) _logger.Warn($"Could not resolve local IP, falling back to loopback: {e.Message}");
            localIp = IPAddress.Loopback;
        }

        IPAddress? configuredExternalIp = TryGetExternalIpOverride(_networkConfig.ExternalIp, nameof(NetworkConfig.ExternalIp), expectedFamily: null);
        IPAddress? configuredExternalIpV4 = TryGetExternalIpOverride(_networkConfig.ExternalIpV4, nameof(NetworkConfig.ExternalIpV4), AddressFamily.InterNetwork);
        IPAddress? configuredExternalIpV6 = TryGetExternalIpOverride(_networkConfig.ExternalIpV6, nameof(NetworkConfig.ExternalIpV6), AddressFamily.InterNetworkV6);

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

        return new ConfiguredAddresses(
            localIp,
            configuredExternalIp,
            configuredExternalIpV4 ?? IIPResolver.NethermindIp.NormalizeExternalIp(configuredExternalIp, AddressFamily.InterNetwork),
            configuredExternalIpV6 ?? IIPResolver.NethermindIp.NormalizeExternalIp(configuredExternalIp, AddressFamily.InterNetworkV6));
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

    private async Task<IPAddress?> ResolveExternalIp(AddressFamily family, CancellationToken cancellationToken)
    {
        try
        {
            foreach (IIPSource source in _externalIpSources(family))
            {
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    (bool success, IPAddress ip) = await source.TryGetIP(cancellationToken);
                    if (!success)
                    {
                        continue;
                    }

                    IPAddress? externalIp = IIPResolver.NethermindIp.NormalizeExternalIp(ip, family);
                    if (externalIp is not null &&
                        !externalIp.IsLoopbackOrPrivateOrLinkLocal &&
                        !externalIp.IsMulticast &&
                        !externalIp.IsSpecialUseAddress)
                    {
                        return externalIp;
                    }

                    if (_logger.IsDebug) _logger.Debug($"External IP source returned an unusable {GetFamilyName(family)} address.");
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception e)
                {
                    _logger.DebugError($"Error while resolving external {GetFamilyName(family)} address", e);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception e)
        {
            _logger.DebugError($"Error while enumerating external {GetFamilyName(family)} address sources", e);
        }

        if (_logger.IsDebug) _logger.Debug($"External {GetFamilyName(family)} address could not be resolved.");
        return null;
    }

    private async Task<IPAddress?> ResolveAutomaticExternalIp(AddressFamily family, CancellationToken cancellationToken)
    {
        IPAddress? resolved = await ResolveExternalIp(family, cancellationToken);
        long now = _timeProvider.GetTimestamp();
        if (resolved is not null)
        {
            AutoResolvedIp current = new(resolved, now);
            if (family == AddressFamily.InterNetwork)
            {
                _lastExternalIpV4 = current;
            }
            else
            {
                _lastExternalIpV6 = current;
            }

            return resolved;
        }

        AutoResolvedIp? previous = family == AddressFamily.InterNetwork
            ? _lastExternalIpV4
            : _lastExternalIpV6;
        if (previous is not null &&
            _timeProvider.GetElapsedTime(previous.ResolvedAtTimestamp, now) <= AutoAddressMaxStaleAge)
        {
            if (_logger.IsDebug) _logger.Debug($"Retaining the last resolved external {GetFamilyName(family)} address after a failed refresh.");
            return previous.Address;
        }

        return null;
    }

    private static IEnumerable<IIPSource> CreateExternalIpSources(AddressFamily family, ILogManager logManager)
    {
        switch (family)
        {
            case AddressFamily.InterNetwork:
                yield return new WebIPSource("https://api.ipify.org", logManager);
                yield return new WebIPSource("https://4.ident.me", logManager);
                break;
            case AddressFamily.InterNetworkV6:
                yield return new WebIPSource("https://api6.ipify.org", logManager);
                yield return new WebIPSource("https://6.ident.me", logManager);
                break;
        }
    }

    private static string GetFamilyName(AddressFamily family)
        => family switch
        {
            AddressFamily.InterNetwork => "IPv4",
            AddressFamily.InterNetworkV6 => "IPv6",
            _ => family.ToString()
        };

    private static bool HasLocalAddressFamily(AddressFamily family)
    {
        if (family switch
        {
            AddressFamily.InterNetwork => !Socket.OSSupportsIPv4,
            AddressFamily.InterNetworkV6 => !Socket.OSSupportsIPv6,
            _ => true
        })
        {
            return false;
        }

        try
        {
            foreach (NetworkInterface networkInterface in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (networkInterface.OperationalStatus != OperationalStatus.Up ||
                    networkInterface.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                {
                    continue;
                }

                foreach (UnicastIPAddressInformation unicastAddress in networkInterface.GetIPProperties().UnicastAddresses)
                {
                    IPAddress address = unicastAddress.Address;
                    if (IsUsableLocalAddress(address, family))
                    {
                        return true;
                    }
                }
            }
        }
        catch (Exception e) when (e is NetworkInformationException or PlatformNotSupportedException)
        {
            return true;
        }

        return false;
    }

    internal static bool IsUsableLocalAddress(IPAddress address, AddressFamily family)
    {
        if (DiscoveryAddressSupport.GetFamily(address) != family ||
            address.IsWildcardOrNone || IPAddress.IsLoopback(address) || address.IsIPv6LinkLocal)
        {
            return false;
        }

        if (family != AddressFamily.InterNetwork)
        {
            return true;
        }

        Span<byte> bytes = stackalloc byte[4];
        return address.TryWriteBytes(bytes, out _) && bytes is not [169, 254, ..];
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

        if (_logger.IsInfo) _logger.Info($"Using the external IP override: {nameof(NetworkConfig)}.{configName} = {ipOverride}");
        return normalizedIp;
    }

    private async Task<IPAddress> InitializeLocalIp(CancellationToken cancellationToken)
    {
        IEnumerable<IIPSource> GetIPSources()
        {
            yield return new NetworkConfigLocalIPSource(_networkConfig, _logManager);
        }

        try
        {
            foreach (IIPSource s in GetIPSources())
            {
                (bool success, IPAddress ip) = await s.TryGetIP(cancellationToken);
                if (success)
                {
                    return ip;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception e)
        {
            if (_logger.IsError) _logger.Error("Error while getting local ip", e);
        }

        return IPAddress.Any;
    }

    private sealed record AutoResolvedIp(IPAddress Address, long ResolvedAtTimestamp);

    private sealed record ConfiguredAddresses(IPAddress LocalIp, IPAddress? ExternalIp, IPAddress? ExternalIpV4, IPAddress? ExternalIpV6);
}
