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

public class IPResolver : IIPResolver
{
    private static readonly TimeSpan ResolutionCacheDuration = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan AutoAddressMaxStaleAge = TimeSpan.FromHours(1);

    private readonly ILogger _logger;
    private readonly ILogManager _logManager;
    private readonly INetworkConfig _networkConfig;
    private readonly Func<AddressFamily, IEnumerable<IIPSource>> _externalIpSources;
    private readonly TimeProvider _timeProvider;

    private readonly Lock _lock = new();
    private Task<IIPResolver.NethermindIp>? _resolveTask;
    private long _resolvedAtTimestamp;
    private int _usesAutomaticResolution;
    private AutoResolvedIp? _lastExternalIpV4;
    private AutoResolvedIp? _lastExternalIpV6;

    public event EventHandler? Changed;

    public IPResolver(INetworkConfig networkConfig, ILogManager logManager)
        : this(networkConfig, logManager, externalIpSources: null)
    {
    }

    internal IPResolver(
        INetworkConfig networkConfig,
        ILogManager logManager,
        Func<AddressFamily, IEnumerable<IIPSource>>? externalIpSources,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(networkConfig);
        ArgumentNullException.ThrowIfNull(logManager);

        _networkConfig = networkConfig;
        _logManager = logManager;
        _logger = logManager.GetClassLogger<IPResolver>();
        _externalIpSources = externalIpSources ?? (family => CreateExternalIpSources(family, logManager));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public ValueTask<IIPResolver.NethermindIp> Resolve(CancellationToken cancellationToken = default)
    {
        Task<IIPResolver.NethermindIp>? task = Volatile.Read(ref _resolveTask);
        TaskCompletionSource<IIPResolver.NethermindIp>? completion = null;
        IIPResolver.NethermindIp? previous = null;
        if (NeedsRefresh(task))
        {
            lock (_lock)
            {
                if (NeedsRefresh(_resolveTask))
                {
                    // The shared resolution is never bound to one caller's token. Per-call cancellation is
                    // honored by WaitAsync below, without cancelling or faulting the cached operation.
                    previous = _resolveTask is { IsCompletedSuccessfully: true }
                        ? _resolveTask.Result
                        : null;
                    completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    task = _resolveTask = completion.Task;
                }
                else
                {
                    task = _resolveTask;
                }
            }
        }

        if (completion is not null)
        {
            _ = CompleteResolution(completion, previous);
        }

        return new ValueTask<IIPResolver.NethermindIp>(task!.WaitAsync(cancellationToken));
    }

    private bool NeedsRefresh(Task<IIPResolver.NethermindIp>? task)
        => task is null ||
           task.IsFaulted ||
           task.IsCanceled ||
           (task.IsCompletedSuccessfully &&
            Volatile.Read(ref _usesAutomaticResolution) != 0 &&
            _timeProvider.GetElapsedTime(Volatile.Read(ref _resolvedAtTimestamp)) >= ResolutionCacheDuration);

    private async Task CompleteResolution(
        TaskCompletionSource<IIPResolver.NethermindIp> completion,
        IIPResolver.NethermindIp? previous)
    {
        try
        {
            (IIPResolver.NethermindIp result, bool usedAutomaticResolution) = await ResolveCore();
            Volatile.Write(ref _resolvedAtTimestamp, _timeProvider.GetTimestamp());
            Volatile.Write(ref _usesAutomaticResolution, usedAutomaticResolution ? 1 : 0);
            completion.SetResult(result);
            if (previous is { } previousResult && previousResult != result)
            {
                OnChanged();
            }
        }
        catch (Exception e)
        {
            completion.SetException(e);
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
                if (_logger.IsError) _logger.Error($"An {nameof(IIPResolver)} change subscriber failed.", e);
            }
        }
    }

    private async Task<(IIPResolver.NethermindIp Result, bool UsedAutomaticResolution)> ResolveCore()
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

        IPAddress? externalIpV4 = configuredExternalIpV4 ??
            IIPResolver.NethermindIp.NormalizeExternalIp(configuredExternalIp, AddressFamily.InterNetwork);
        IPAddress? externalIpV6 = configuredExternalIpV6 ??
            IIPResolver.NethermindIp.NormalizeExternalIp(configuredExternalIp, AddressFamily.InterNetworkV6);
        bool resolveExternalIpV4 = externalIpV4 is null;
        bool resolveExternalIpV6 = externalIpV6 is null;

        // Start both missing-family lookups before awaiting either.
        Task<IPAddress?> externalIpV4Task = resolveExternalIpV4
            ? ResolveAutomaticExternalIp(AddressFamily.InterNetwork)
            : Task.FromResult(externalIpV4);
        Task<IPAddress?> externalIpV6Task = resolveExternalIpV6
            ? ResolveAutomaticExternalIp(AddressFamily.InterNetworkV6)
            : Task.FromResult(externalIpV6);

        externalIpV4 = await externalIpV4Task;
        externalIpV6 = await externalIpV6Task;

        IPAddress externalIp = configuredExternalIp
            ?? externalIpV4
            ?? externalIpV6
            ?? IPAddress.None;

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

        if (!externalIp.IsWildcardOrNone)
        {
            ThisNodeInfo.AddInfo("External IP  :", $"{externalIp}");
        }

        if (externalIpV4 is null && externalIpV6 is null && _logger.IsWarn)
        {
            _logger.Warn("External IP could not be resolved. Peers will not be able to connect.");
        }

        return (
            new IIPResolver.NethermindIp(localIp, externalIp, externalIpV4, externalIpV6),
            resolveExternalIpV4 || resolveExternalIpV6);
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

    private async Task<IPAddress?> ResolveExternalIp(AddressFamily family)
    {
        try
        {
            foreach (IIPSource source in _externalIpSources(family))
            {
                try
                {
                    (bool success, IPAddress ip) = await source.TryGetIP();
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
                catch (Exception e)
                {
                    _logger.DebugError($"Error while resolving external {GetFamilyName(family)} address", e);
                }
            }
        }
        catch (Exception e)
        {
            _logger.DebugError($"Error while enumerating external {GetFamilyName(family)} address sources", e);
        }

        if (_logger.IsDebug) _logger.Debug($"External {GetFamilyName(family)} address could not be resolved.");
        return null;
    }

    private async Task<IPAddress?> ResolveAutomaticExternalIp(AddressFamily family)
    {
        IPAddress? resolved = await ResolveExternalIp(family);
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

    private sealed record AutoResolvedIp(IPAddress Address, long ResolvedAtTimestamp);
}
