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
            IIPResolver.NethermindIp ip = await ipResolver.Resolve(CancellationToken.None);
            LocalNodeRecordState state = CreateState(head, ip);
            if (current.State == state)
            {
                return current;
            }

            return CreateSignedRecord(state, NextSequence(current.Record.EnrSequence));
        }
        catch (Exception e)
        {
            if (_logger.IsDebug) _logger.Debug($"Failed to refresh local ENR. {e}");
            return current;
        }
    }

    private async Task<LocalNodeRecord> PrepareNodeRecord(BlockHeader? effectiveHeader, ulong previousSequence, CancellationToken cancellationToken)
    {
        IIPResolver.NethermindIp ip = await ipResolver.Resolve(cancellationToken);
        LocalNodeRecordState state = CreateState(effectiveHeader, ip);
        LogEndpointIssues(ip, state);
        return CreateSignedRecord(state, NextSequence(previousSequence));
    }

    private LocalNodeRecordState CreateState(BlockHeader? effectiveHeader, IIPResolver.NethermindIp ip)
    {
        BlockHeader? header = GetEffectiveHeader(effectiveHeader);
        NetworkForkId currentForkId = forkInfo.GetForkId(header?.Number ?? 0, header?.Timestamp ?? 0);

        (IPAddress? externalIpV4, IPAddress? externalIpV6) = DiscoveryAddressSupport.SelectAdvertised(
            ip.LocalIp,
            ip.ExternalIpV4,
            ip.ExternalIpV6);

        return new LocalNodeRecordState(externalIpV4, externalIpV6, networkConfig.P2PPort, networkConfig.DiscoveryPort, currentForkId);
    }

    private void LogEndpointIssues(IIPResolver.NethermindIp ip, LocalNodeRecordState state)
    {
        if (!_logger.IsWarn)
        {
            return;
        }

        if (ip.ExternalIpV4 is not null && state.ExternalIpV4 is null)
        {
            _logger.Warn("External IPv4 address is available but not advertised because the node does not listen on IPv4 (set LocalIp to an IPv4 address or ::).");
        }

        if (ip.ExternalIpV6 is not null && state.ExternalIpV6 is null)
        {
            _logger.Warn("External IPv6 address is available but not advertised because the node does not listen on IPv6 (set LocalIp to an IPv6 address).");
        }

        if (state.ExternalIpV4 is null && state.ExternalIpV6 is null)
        {
            _logger.Warn("No external IP address is advertised; the node will not be discoverable by peers.");
        }
    }

    private BlockHeader? GetEffectiveHeader(BlockHeader? preferredHeader) => preferredHeader ?? blockTree.Head?.Header ?? blockTree.Genesis;

    private LocalNodeRecord CreateSignedRecord(LocalNodeRecordState state, ulong sequence)
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
            // Some ENR consumers do not implement EIP-778's fallback from tcp6/udp6 to tcp/udp.
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

        return new LocalNodeRecord(selfNodeRecord, state);
    }

    private ulong NextSequence(ulong previous)
    {
        ulong now = timestamper.UnixTime.Milliseconds;
        return now > previous ? now : previous + 1;
    }

    private sealed record LocalNodeRecord(NodeRecord Record, LocalNodeRecordState State);

    private readonly record struct LocalNodeRecordState(
        IPAddress? ExternalIpV4,
        IPAddress? ExternalIpV6,
        int TcpPort,
        int UdpPort,
        NetworkForkId ForkId);
}
