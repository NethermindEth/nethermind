// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.Stats.Model;

namespace Nethermind.Network;

/// <summary>
/// Parses <c>Network.StaticPeerHosts</c>: a comma-separated list of <c>ip[:port]</c>, <c>[ipv6]:port</c>,
/// <c>cidr</c> and <c>hostname[:port]</c> entries, each with an optional <c>enode://*@</c> prefix.
/// Hostnames are resolved in the background on startup and then every <see cref="RefreshInterval"/>.
/// </summary>
public sealed class StaticHostFilter : IStaticHostFilter, IDisposable
{
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(5);
    public static readonly StaticHostFilter Empty = new(null, static (_, _) => Task.FromResult(Array.Empty<IPAddress>()), LimboLogs.Instance.GetClassLogger<StaticHostFilter>());

    private const string AnyKeyEnodePrefix = "enode://*@";

    private readonly Rule[] _fixedRules;
    private readonly HostEntry[] _hosts;
    private readonly Func<string, CancellationToken, Task<IPAddress[]>> _resolve;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConditionalWeakTable<Node, Promotion> _promoted = [];
    private readonly ConditionalWeakTable<Node, IPEndPoint> _proven = [];
    private volatile Rule[] _rules;
    private int _disposed;

    public event EventHandler? Changed;

    public StaticHostFilter(INetworkConfig networkConfig, ILogManager logManager)
        : this(networkConfig.StaticPeerHosts, Dns.GetHostAddressesAsync, logManager.GetClassLogger<StaticHostFilter>())
    {
        if (_hosts.Length > 0)
        {
            CancellationToken token = _cts.Token;
            _ = Task.Run(() => RefreshLoopAsync(token));
        }
    }

    internal StaticHostFilter(string? config, Func<string, CancellationToken, Task<IPAddress[]>> resolve, ILogger logger)
    {
        _resolve = resolve;
        _logger = logger;
        (_fixedRules, _hosts) = Parse(config, logger);
        _rules = _fixedRules;
    }

    public bool IsMatch(IPAddress address) => IsMatch(address, 0, checkPort: false);

    public bool IsMatch(IPEndPoint endPoint) => IsMatch(endPoint.Address, endPoint.Port, checkPort: true);

    private bool IsMatch(IPAddress address, int port, bool checkPort)
    {
        Rule[] rules = _rules;
        if (rules.Length == 0) return false;

        address = Normalize(address);
        foreach (Rule rule in rules)
        {
            if ((!checkPort || rule.Port == 0 || rule.Port == port) && rule.Network.Contains(address))
            {
                return true;
            }
        }

        return false;
    }

    public bool TryPromote(Node node, bool matchPort)
    {
        if (node.IsStatic || !(matchPort ? IsMatch(node.Address) : IsMatch(node.Address.Address))) return false;

        node.IsStatic = true;
        _promoted.AddOrUpdate(node, matchPort ? Promotion.EndPoint : Promotion.Address);
        return true;
    }

    public void Demote(Node node)
    {
        if (_promoted.Remove(node))
        {
            node.IsStatic = false;
        }
    }

    public void Untrack(Node node) => _promoted.Remove(node);

    public bool IsPromoted(Node node) => _promoted.TryGetValue(node, out _);

    public bool IsUnproven(Node node) => _promoted.TryGetValue(node, out Promotion? promotion) && promotion.MatchPort && !IsProven(node);

    public void MarkProven(Node node, IPEndPoint remote)
    {
        // A dial can fall back to another address of the node record, which proves nothing about the pooled address.
        if ((_fixedRules.Length == 0 && _hosts.Length == 0) || !SameEndPoint(remote, node.Address)) return;

        _proven.AddOrUpdate(node, node.Address);
        TryPromote(node, matchPort: true);
    }

    public bool IsProven(Node node) => _proven.TryGetValue(node, out IPEndPoint? endPoint) && endPoint.Equals(node.Address);

    internal async Task RefreshAsync(CancellationToken token)
    {
        bool changed = false;
        foreach (HostEntry host in _hosts)
        {
            try
            {
                IPAddress[] addresses = await _resolve(host.Name, token);
                if (!SameAddresses(host.Addresses, addresses))
                {
                    host.Addresses = addresses;
                    changed = true;
                    if (_logger.IsInfo) _logger.Info($"Static peer host {host.Name} resolved to {string.Join<IPAddress>(", ", addresses)}");
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // Keep the last known addresses, so a DNS outage does not drop the peers.
                if (_logger.IsWarn) _logger.Warn($"Cannot resolve static peer host {host.Name}: {e.Message}");
            }
        }

        if (!changed) return;

        List<Rule> rules = [.. _fixedRules];
        foreach (HostEntry host in _hosts)
        {
            foreach (IPAddress address in host.Addresses)
            {
                rules.Add(new Rule(SingleAddress(address), host.Port));
            }
        }

        _rules = rules.ToArray();

        List<Node> stale = [];
        foreach (KeyValuePair<Node, Promotion> promoted in _promoted)
        {
            Node node = promoted.Key;
            bool matches = promoted.Value.MatchPort ? IsMatch(node.Address) : IsMatch(node.Address.Address);
            if (!matches) stale.Add(node);
        }

        foreach (Node node in stale)
        {
            Demote(node);
        }

        try
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception e)
        {
            if (_logger.IsError) _logger.Error("Static peer host change handler failed.", e);
        }
    }

    private async Task RefreshLoopAsync(CancellationToken token)
    {
        try
        {
            using PeriodicTimer timer = new(RefreshInterval);
            do
            {
                await RefreshAsync(token);
            }
            while (await timer.WaitForNextTickAsync(token));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e)
        {
            if (_logger.IsError) _logger.Error("Static peer host refresh failed.", e);
        }
    }

    private static (Rule[], HostEntry[]) Parse(string? config, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(config)) return ([], []);

        List<Rule> rules = [];
        List<HostEntry> hosts = [];
        foreach (string raw in config.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            ReadOnlySpan<char> entry = raw.StartsWith(AnyKeyEnodePrefix, StringComparison.OrdinalIgnoreCase)
                ? raw.AsSpan(AnyKeyEnodePrefix.Length)
                : raw;

            if (entry.Contains('/'))
            {
                if (IPNetwork.TryParse(entry, out IPNetwork network))
                {
                    rules.Add(new Rule(network, 0));
                    continue;
                }
            }
            else if (IPEndPoint.TryParse(entry, out IPEndPoint? endPoint))
            {
                rules.Add(new Rule(SingleAddress(endPoint.Address), endPoint.Port));
                continue;
            }
            else if (TryParseHost(entry, out string? name, out int port))
            {
                hosts.Add(new HostEntry(name, port));
                continue;
            }

            if (logger.IsWarn) logger.Warn($"Ignoring invalid {nameof(INetworkConfig.StaticPeerHosts)} entry: {raw}");
        }

        return (rules.ToArray(), hosts.ToArray());
    }

    private static bool TryParseHost(ReadOnlySpan<char> entry, [NotNullWhen(true)] out string? name, out int port)
    {
        port = 0;
        int colon = entry.LastIndexOf(':');
        if (colon >= 0)
        {
            if (!int.TryParse(entry[(colon + 1)..], out port) || port is <= 0 or > IPEndPoint.MaxPort)
            {
                name = null;
                return false;
            }

            entry = entry[..colon];
        }

        name = entry.ToString();
        return Uri.CheckHostName(name) == UriHostNameType.Dns;
    }

    private static IPAddress Normalize(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    private static bool SameEndPoint(IPEndPoint a, IPEndPoint b) => a.Port == b.Port && Normalize(a.Address).Equals(Normalize(b.Address));

    private static IPNetwork SingleAddress(IPAddress address)
    {
        address = Normalize(address);
        return new IPNetwork(address, address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128);
    }

    private static bool SameAddresses(IPAddress[] current, IPAddress[] resolved)
    {
        if (current.Length != resolved.Length) return false;
        foreach (IPAddress address in resolved)
        {
            if (Array.IndexOf(current, address) < 0) return false;
        }

        return true;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;

        _cts.Cancel();
        _cts.Dispose();
    }

    private readonly record struct Rule(IPNetwork Network, int Port);

    private sealed class Promotion(bool matchPort)
    {
        public static readonly Promotion EndPoint = new(true);
        public static readonly Promotion Address = new(false);

        public bool MatchPort { get; } = matchPort;
    }

    private sealed class HostEntry(string name, int port)
    {
        public string Name { get; } = name;
        public int Port { get; } = port;
        public IPAddress[] Addresses { get; set; } = [];
    }
}
