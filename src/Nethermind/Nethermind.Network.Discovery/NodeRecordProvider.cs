// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac.Features.AttributeFilters;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.Network.Enr;
using System.Net;
using System.Net.Sockets;
using NetworkForkId = Nethermind.Network.ForkId;

namespace Nethermind.Network.Discovery;

public sealed class NodeRecordProvider(
    [KeyFilter(IProtectedPrivateKey.NodeKey)] IProtectedPrivateKey nodeKey,
    IIPResolver ipResolver,
    IEthereumEcdsa ethereumEcdsa,
    INetworkConfig networkConfig,
    IBlockTree blockTree,
    IForkInfo forkInfo,
    ITimestamper timestamper,
    ILogManager logManager
) : INodeRecordProvider
{
    private readonly Lock _lock = new();
    private readonly NodeRecordSigner _enrSigner = new(ethereumEcdsa, nodeKey.Unprotect());
    private readonly ILogger _logger = logManager.GetClassLogger<NodeRecordProvider>();
    private Task<LocalNodeRecord>? _nodeRecordTask;
    private bool _subscribed;

    public async ValueTask<NodeRecord> GetCurrentAsync(CancellationToken cancellationToken = default)
    {
        Task<LocalNodeRecord>? task = Volatile.Read(ref _nodeRecordTask);
        if (task is null)
        {
            lock (_lock)
            {
                if (!_subscribed)
                {
                    blockTree.NewHeadBlock += OnNewHeadBlock;
                    _subscribed = true;
                }

                // Build once, guarding concurrent callers (Ping/HandleEnrRequest run from concurrent
                // discovery handlers). Use CancellationToken.None so the cached ENR isn't faulted by a
                // single caller's token; per-call cancellation is honored via WaitAsync below.
                task = _nodeRecordTask ??= PrepareNodeRecord(GetEffectiveHeader(null), previousSequence: 0, CancellationToken.None);
            }
        }

        return (await task.WaitAsync(cancellationToken)).Record;
    }

    private void OnNewHeadBlock(object? sender, BlockEventArgs e)
    {
        Task<LocalNodeRecord>? task = Volatile.Read(ref _nodeRecordTask);
        if (task is null)
        {
            return;
        }

        lock (_lock)
        {
            task = _nodeRecordTask;
            if (task is not null)
            {
                _nodeRecordTask = RefreshNodeRecord(task, e.Block.Header);
            }
        }
    }

    private async Task<LocalNodeRecord> RefreshNodeRecord(Task<LocalNodeRecord> currentTask, BlockHeader head)
    {
        LocalNodeRecord current = await currentTask;
        try
        {
            (LocalNodeRecordState state, EndpointIssues endpointIssues) = await CreateState(head, CancellationToken.None);
            if (current.EndpointIssues != endpointIssues)
            {
                LogEndpointIssues(endpointIssues);
            }

            if (current.State == state)
            {
                return current.EndpointIssues == endpointIssues
                    ? current
                    : current with { EndpointIssues = endpointIssues };
            }

            return CreateSignedRecord(state, endpointIssues, NextSequence(current.Record.EnrSequence));
        }
        catch (Exception e)
        {
            if (_logger.IsDebug) _logger.Debug($"Failed to refresh local ENR. {e}");
            return current;
        }
    }

    private async Task<LocalNodeRecord> PrepareNodeRecord(BlockHeader? effectiveHeader, ulong previousSequence, CancellationToken cancellationToken)
    {
        (LocalNodeRecordState state, EndpointIssues endpointIssues) = await CreateState(effectiveHeader, cancellationToken);
        LogEndpointIssues(endpointIssues);
        return CreateSignedRecord(state, endpointIssues, NextSequence(previousSequence));
    }

    private async ValueTask<(LocalNodeRecordState State, EndpointIssues EndpointIssues)> CreateState(
        BlockHeader? effectiveHeader,
        CancellationToken cancellationToken)
    {
        IIPResolver.NethermindIp ip = await ipResolver.Resolve(cancellationToken);
        BlockHeader? header = GetEffectiveHeader(effectiveHeader);
        NetworkForkId currentForkId = forkInfo.GetForkId(header?.Number ?? 0, header?.Timestamp ?? 0);

        // RLPx and discovery each bind a single socket to LocalIp, so advertise an address family only
        // when that socket can receive it; otherwise peers would dial an endpoint nothing is listening on.
        IPAddress? resolvedExternalIpV4 = ip.ExternalIpV4;
        IPAddress? externalIpV4 = CompositeDiscoveryApp.SupportsAddressFamily(ip.LocalIp, AddressFamily.InterNetwork)
            ? resolvedExternalIpV4
            : null;
        IPAddress? resolvedExternalIpV6 = ip.ExternalIpV6;
        IPAddress? externalIpV6 = CompositeDiscoveryApp.SupportsAddressFamily(ip.LocalIp, AddressFamily.InterNetworkV6)
            ? resolvedExternalIpV6
            : null;
        EndpointIssues endpointIssues = EndpointIssues.None;

        if (resolvedExternalIpV4 is not null && externalIpV4 is null)
        {
            endpointIssues |= EndpointIssues.IPv4NotAdvertised;
        }

        if (resolvedExternalIpV6 is not null && externalIpV6 is null)
        {
            endpointIssues |= EndpointIssues.IPv6NotAdvertised;
        }

        if (externalIpV4 is null && externalIpV6 is null)
        {
            endpointIssues |= EndpointIssues.NoExternalIpAdvertised;
        }

        LocalNodeRecordState state = new(externalIpV4, externalIpV6, networkConfig.P2PPort, networkConfig.DiscoveryPort, currentForkId);
        return (state, endpointIssues);
    }

    private void LogEndpointIssues(EndpointIssues endpointIssues)
    {
        if (!_logger.IsWarn)
        {
            return;
        }

        if ((endpointIssues & EndpointIssues.IPv4NotAdvertised) != 0)
        {
            _logger.Warn("External IPv4 address is available but not advertised because the node does not listen on IPv4 (set LocalIp to an IPv4 address or ::).");
        }

        if ((endpointIssues & EndpointIssues.IPv6NotAdvertised) != 0)
        {
            _logger.Warn("External IPv6 address is available but not advertised because the node does not listen on IPv6 (set LocalIp to an IPv6 address).");
        }

        if ((endpointIssues & EndpointIssues.NoExternalIpAdvertised) != 0)
        {
            _logger.Warn("No external IP address is advertised; the node will not be discoverable by peers.");
        }
    }

    private BlockHeader? GetEffectiveHeader(BlockHeader? preferredHeader) => preferredHeader ?? blockTree.Head?.Header ?? blockTree.Genesis;

    private LocalNodeRecord CreateSignedRecord(LocalNodeRecordState state, EndpointIssues endpointIssues, ulong sequence)
    {
        NodeRecord selfNodeRecord = new();
        selfNodeRecord.SetEntry(new EthEntry(state.ForkId.HashBytes, state.ForkId.Next));
        if (state.ExternalIpV4 is not null)
        {
            selfNodeRecord.SetEntry(new IpEntry(state.ExternalIpV4));
            selfNodeRecord.SetEntry(new TcpEntry(state.TcpPort));
            selfNodeRecord.SetEntry(new UdpEntry(state.UdpPort));
        }

        if (state.ExternalIpV6 is not null)
        {
            selfNodeRecord.SetEntry(new Ip6Entry(state.ExternalIpV6));
            selfNodeRecord.SetEntry(new Tcp6Entry(state.TcpPort));
            selfNodeRecord.SetEntry(new Udp6Entry(state.UdpPort));
        }
        selfNodeRecord.SetEntry(new SecP256k1Entry(nodeKey.CompressedPublicKey));
        selfNodeRecord.EnrSequence = sequence;
        _enrSigner.Sign(selfNodeRecord);
        if (!_enrSigner.Verify(selfNodeRecord))
        {
            throw new NetworkingException("Self ENR initialization failed", NetworkExceptionType.Discovery);
        }

        return new LocalNodeRecord(selfNodeRecord, state, endpointIssues);
    }

    private ulong NextSequence(ulong previous)
    {
        ulong now = timestamper.UnixTime.Milliseconds;
        return now > previous ? now : previous + 1;
    }

    private sealed record LocalNodeRecord(NodeRecord Record, LocalNodeRecordState State, EndpointIssues EndpointIssues);

    private readonly record struct LocalNodeRecordState(
        IPAddress? ExternalIpV4,
        IPAddress? ExternalIpV6,
        int TcpPort,
        int UdpPort,
        NetworkForkId ForkId);

    [Flags]
    private enum EndpointIssues
    {
        None = 0,
        IPv4NotAdvertised = 1,
        IPv6NotAdvertised = 2,
        NoExternalIpAdvertised = 4
    }
}
